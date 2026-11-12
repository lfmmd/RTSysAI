"""
In-process write test on the OPEN project HMI + UI repaint verification.

- Reads the selected control (ZHMI_GetEditObject) from the running RTSys.
- Records geometry, moves X by +30 via SetFeatureInt (remote thread stub).
- Forces the designer window to repaint (InvalidateRect/RedrawWindow/WM_PAINT).
- Grabs before/after screenshots and compares pixels.
- Restores the original X and verifies the UI returns.
No file is saved; all values are restored.
"""
import sys, os, json, time, struct, ctypes
from ctypes import wintypes as wt
from PIL import Image, ImageGrab

TOOLS = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, TOOLS)
import hmi_probe as H
import hmi_api_test as A

k32 = H.k32
user32 = ctypes.WinDLL('user32', use_last_error=True)
gdi32 = ctypes.WinDLL('gdi32', use_last_error=True)

user32.GetWindowThreadProcessId.argtypes = [ctypes.c_void_p, ctypes.POINTER(wt.DWORD)]
user32.IsWindowVisible.argtypes = [ctypes.c_void_p]
user32.GetWindowTextW.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_wchar), ctypes.c_int]
user32.GetClassNameW.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_wchar), ctypes.c_int]
user32.GetWindowRect.argtypes = [ctypes.c_void_p, ctypes.POINTER(wt.RECT)]
user32.SetForegroundWindow.argtypes = [ctypes.c_void_p]
user32.ShowWindow.argtypes = [ctypes.c_void_p, ctypes.c_int]
user32.InvalidateRect.argtypes = [ctypes.c_void_p, ctypes.c_void_p, wt.BOOL]
user32.UpdateWindow.argtypes = [ctypes.c_void_p]
user32.RedrawWindow.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, wt.UINT]
user32.PostMessageW.argtypes = [ctypes.c_void_p, wt.UINT, ctypes.c_void_p, ctypes.c_void_p]

EnumProc = ctypes.WINFUNCTYPE(wt.BOOL, ctypes.c_void_p, ctypes.c_void_p)
user32.EnumWindows.argtypes = [EnumProc, ctypes.c_void_p]
user32.EnumChildWindows.argtypes = [ctypes.c_void_p, EnumProc, ctypes.c_void_p]

def win_title(hwnd):
    buf = ctypes.create_unicode_buffer(512)
    user32.GetWindowTextW(hwnd, buf, 512)
    return buf.value

def win_class(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, buf, 256)
    return buf.value

def win_rect(hwnd):
    r = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return (r.left, r.top, r.right, r.bottom)

def pid_of(hwnd):
    pid = wt.DWORD(0)
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return pid.value

def find_main(pid):
    found = []
    def cb(hwnd, lp):
        if pid_of(hwnd) == pid and user32.IsWindowVisible(hwnd):
            found.append(hwnd)
        return True
    user32.EnumWindows(EnumProc(cb), None)
    best = None; best_area = 0
    for h in found:
        l,t,r,b = win_rect(h)
        area = max(0, r-l) * max(0, b-t)
        if area > best_area:
            best_area = area; best = h
    return best

def find_mdi_child(main, needles=('.hmi', 'Hmi')):
    found = []
    def cb(hwnd, lp):
        if not user32.IsWindowVisible(hwnd):
            return True
        title = win_title(hwnd)
        for n in needles:
            if n.lower() in title.lower():
                found.append(hwnd); break
        return True
    user32.EnumChildWindows(main, EnumProc(cb), None)
    if not found:
        return None
    # largest matching child
    found.sort(key=lambda h: (lambda x: max(0,x[2]-x[0])*max(0,x[3]-x[1]))(win_rect(h)), reverse=True)
    return found[0]

def grab(rect, path):
    img = ImageGrab.grab(bbox=rect, all_screens=True)
    img.save(path)
    return img

def diff(a, b):
    if a.size != b.size:
        return {'size_changed': True, 'pixels': -1, 'bbox': None}
    from PIL import ImageChops
    d = ImageChops.difference(a.convert('RGB'), b.convert('RGB')).convert('L')
    mask = d.point(lambda p: 255 if p > 10 else 0)
    hist = mask.histogram()
    count = hist[255] if len(hist) > 255 else 0
    return {'pixels': count, 'bbox': mask.getbbox()}

def main():
    try:
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass
    pid = H.find_pid('RTSys.exe')
    if not pid:
        print('RTSys.exe not running'); return
    p = H.Proc(pid)
    H.k32.CloseHandle(p.h)
    p.h = H.k32.OpenProcess(0x0010 | 0x0020 | 0x0008 | 0x0400, False, pid)
    if not p.h:
        print('cannot open RTSys.exe for write'); return
    ext = H.enum_modules(pid)['extmainhmi.dll'][0]
    R = lambda off: ext + off
    set_int = R(0x1D3E0)

    edit = p.u32(ext + 0x5DC2C)
    if not edit:
        print('no selected object (select a control in the HMI designer)'); return
    etype = p.u32(edit + 4)
    fileptr = p.u32(edit + 0x10)
    print('edit', hex(edit), 'type', etype, 'file', hex(fileptr))
    if not (100 <= etype < 1000):
        print('selected object is not a control (type %d)' % etype); return

    target = edit
    cid = H.fval(p, target, 1000)
    name = H.fval(p, target, 1001)
    ox = H.fval(p, target, 1003, 0)
    oy = H.fval(p, target, 1003, 1)
    ow = H.fval(p, target, 1002, 0)
    oh = H.fval(p, target, 1002, 1)
    f1270 = H.fval(p, target, 1270)
    node_x = H.find_feature(p, target, 1003, 0)
    if not ox or not ow:
        print('geometry features missing'); return
    orig_x = ox['int']; orig_w = ow['int']
    lock = f1270['int'] if f1270 else 0
    print('control id=%s type=%s name=%r geo=%dx%d @(%d,%d) 1270=%s' %
          (cid and cid['int'], etype, name and name['string'], orig_w,
           oh and oh['int'], orig_x, oy and oy['int'], lock))

    main_hwnd = find_main(pid)
    if not main_hwnd:
        print('main window not found'); return
    child = find_mdi_child(main_hwnd)
    canvas = child or main_hwnd
    print('main', hex(main_hwnd), repr(win_title(main_hwnd)))
    print('canvas', hex(canvas), repr(win_title(canvas)), win_rect(canvas))

    # bring the app to front so ImageGrab captures it
    user32.ShowWindow(main_hwnd, 9)          # SW_RESTORE
    user32.SetForegroundWindow(main_hwnd)
    time.sleep(0.4)

    rect = win_rect(canvas)
    # clamp to screen
    rect = (max(0, rect[0]), max(0, rect[1]), rect[2], rect[3])
    before_path = os.path.join(TOOLS, 'ui_before.png')
    after_path = os.path.join(TOOLS, 'ui_after.png')
    restored_path = os.path.join(TOOLS, 'ui_restored.png')

    img_before = grab(rect, before_path)
    results = {'pid': pid, 'control': target, 'control_id': cid and cid['int'],
               'control_type': etype, 'control_name': name and name['string'],
               'orig': {'x': orig_x, 'y': oy and oy['int'], 'w': orig_w,
                        'h': oh and oh['int'], 'f1270': lock},
               'canvas_rect': rect, 'window_title_before': win_title(main_hwnd)}

    restored = False
    try:
        if lock:
            A.remote_call(p, set_int, target, [1270, 0, 0, 0])
        rc = A.remote_call(p, set_int, target, [1003, 0, 0, orig_x + 30])
        time.sleep(0.1)
        new_x = H.fval(p, target, 1003, 0)
        results['set_x'] = {'rc': rc, 'x': new_x and new_x['int'],
                            'ok': new_x and new_x['int'] == orig_x + 30}
        print('set X ->', results['set_x'])

        # force repaint of the designer
        user32.InvalidateRect(canvas, None, True)
        user32.UpdateWindow(canvas)
        user32.RedrawWindow(main_hwnd, None, None, 0x0001 | 0x0080 | 0x0100 | 0x0400)  # INVALIDATE|ALLCHILDREN|UPDATENOW|FRAME
        user32.PostMessageW(canvas, 0x000F, None, None)  # WM_PAINT
        time.sleep(1.0)

        img_after = grab(rect, after_path)
        d = diff(img_before, img_after)
        results['ui_after_change'] = d
        results['ui_changed'] = d.get('pixels', 0) > 50
        print('UI diff after change ->', d)

        # restore
        A.remote_call(p, set_int, target, [1003, 0, 0, orig_x])
        if lock:
            A.remote_call(p, set_int, target, [1270, 0, 0, lock])
        time.sleep(0.1)
        back_x = H.fval(p, target, 1003, 0)
        results['restore_x'] = {'x': back_x and back_x['int'],
                                'ok': back_x and back_x['int'] == orig_x}
        user32.InvalidateRect(canvas, None, True)
        user32.UpdateWindow(canvas)
        user32.RedrawWindow(main_hwnd, None, None, 0x0001 | 0x0080 | 0x0100 | 0x0400)
        user32.PostMessageW(canvas, 0x000F, None, None)
        time.sleep(1.0)

        img_restored = grab(rect, restored_path)
        d2 = diff(img_before, img_restored)
        results['ui_after_restore'] = d2
        results['ui_restored'] = d2.get('pixels', 0) <= 50
        results['window_title_after'] = win_title(main_hwnd)
        restored = results['restore_x']['ok']
        print('restore X ->', results['restore_x'], 'UI diff ->', d2)
    finally:
        if not restored:
            try:
                A.remote_call(p, set_int, target, [1003, 0, 0, orig_x])
                if lock:
                    A.remote_call(p, set_int, target, [1270, 0, 0, lock])
                print('best-effort restore executed')
            except Exception as ex:
                print('best-effort restore failed:', ex)
        outpath = os.path.join(TOOLS, 'hmi_ui_write_test_last.json')
        with open(outpath, 'w', encoding='utf-8') as f:
            json.dump(results, f, ensure_ascii=False, indent=1)
        print('JSON ->', outpath)
        p.close()

if __name__ == '__main__':
    main()
