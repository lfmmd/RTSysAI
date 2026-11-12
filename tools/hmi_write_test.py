"""
Reversible in-process write test for RTSys HMI internals.
Calls ZHmi_ControlClass::SetFeatureInt via a remote thread stub, verifies the
change by reading memory, then restores the original value. Never saves files.
"""
import sys, os, json, struct, ctypes
from ctypes import wintypes as wt

TOOLS = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, TOOLS)
import hmi_probe as H

k32 = H.k32
k32.VirtualAllocEx.restype = ctypes.c_void_p
k32.VirtualAllocEx.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, wt.DWORD, wt.DWORD]
k32.VirtualFreeEx.restype = wt.BOOL
k32.VirtualFreeEx.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, wt.DWORD]
k32.WriteProcessMemory.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
                                   ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
k32.CreateRemoteThread.restype = ctypes.c_void_p
k32.CreateRemoteThread.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t,
                                   ctypes.c_void_p, ctypes.c_void_p, wt.DWORD, ctypes.c_void_p]
k32.WaitForSingleObject.restype = wt.DWORD
k32.WaitForSingleObject.argtypes = [ctypes.c_void_p, wt.DWORD]

MEM_COMMIT_RESERVE = 0x3000
PAGE_EXECUTE_READWRITE = 0x40
MEM_RELEASE = 0x8000

def remote_call(p, func, this, args):
    """Build a tiny __thiscall stub, run it in a remote thread, return EAX."""
    mem = k32.VirtualAllocEx(p.h, None, 4096, MEM_COMMIT_RESERVE, PAGE_EXECUTE_READWRITE)
    if not mem:
        raise OSError(ctypes.get_last_error(), 'VirtualAllocEx failed')
    result_addr = mem + 0x200
    code = bytearray()
    code += b'\xB8' + struct.pack('<I', this & 0xFFFFFFFF)      # mov eax, this
    for a in reversed(args):                                     # push args right-to-left
        code += b'\x68' + struct.pack('<I', a & 0xFFFFFFFF)
    code += b'\x8B\xC8'                                          # mov ecx, eax
    code += b'\xB8' + struct.pack('<I', func & 0xFFFFFFFF)       # mov eax, func
    code += b'\xFF\xD0'                                          # call eax
    code += b'\xA3' + struct.pack('<I', result_addr)             # mov [result], eax
    code += b'\xC3'                                              # ret
    written = ctypes.c_size_t(0)
    buf = ctypes.create_string_buffer(bytes(code))
    if not k32.WriteProcessMemory(p.h, ctypes.c_void_p(mem), buf, len(code), ctypes.byref(written)):
        k32.VirtualFreeEx(p.h, ctypes.c_void_p(mem), 0, MEM_RELEASE)
        raise OSError(ctypes.get_last_error(), 'WriteProcessMemory stub failed')
    th = k32.CreateRemoteThread(p.h, None, 0, ctypes.c_void_p(mem), None, 0, None)
    if not th:
        k32.VirtualFreeEx(p.h, ctypes.c_void_p(mem), 0, MEM_RELEASE)
        raise OSError(ctypes.get_last_error(), 'CreateRemoteThread failed')
    k32.WaitForSingleObject(th, 5000)
    k32.CloseHandle(th)
    ret = struct.unpack('<I', p.read(result_addr, 4))[0]
    k32.VirtualFreeEx(p.h, ctypes.c_void_p(mem), 0, MEM_RELEASE)
    return ret

def feat_int(p, obj, main, sub=0):
    n = H.find_feature(p, obj, main, sub)
    if n is None:
        return None, None
    return n, p.i32(n + 0x20)

def feat_double(p, obj, main, sub=0):
    n = H.find_feature(p, obj, main, sub)
    if n is None:
        return None
    return p.f64(n + 0x28)

def main():
    try:
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass
    pid = H.find_pid('RTSys.exe')
    if not pid:
        print('RTSys.exe not running'); return
    p = H.Proc(pid)
    mods = H.enum_modules(pid)
    ext = mods['extmainhmi.dll'][0]
    edit = p.u32(ext + 0x5DC2C)
    fileptr = p.u32(edit + 0x10) if edit else 0
    if not H.is_plausible(fileptr):
        # no selection: locate ZHmi_FileClass via its vtable
        hits, scanned = H.scan_fileclass(p, ext, ext + 0x4973C)
        cands = []
        for a in hits:
            try:
                head = p.u32(a + 0x20)
                if head and not H.is_plausible(head):
                    continue
                wins = H.iter_object_chain(p, a, 0x20) if head else []
                ok = True
                for w in wins[:3]:
                    nodes = H.iter_feature_nodes(p, w)
                    if not nodes:
                        ok = False; break
                    if not (1000 <= p.i32(nodes[0]) <= 30000):
                        ok = False; break
                if ok:
                    cands.append((len(wins), a))
            except OSError:
                continue
        if not cands:
            print('no HMI FileClass found (open an HMI in RTSys)'); return
        cands.sort(reverse=True)
        fileptr = cands[0][1]
        print('file object via vtable scan:', hex(fileptr), 'windows', cands[0][0])
    if not H.is_plausible(fileptr):
        print('no file object'); return

    func_set = ext + 0x1D3E0   # ?SetFeatureInt@ZHmi_ControlClass@@UAEIHHHH@Z
    func_get = ext + 0xEB30   # ?GetFeatureInt@ZHmi_BaseObject@@UAEHHHH@Z
    expect_set = bytes.fromhex('558bec568b750857ff75108bf9ff750c')
    got = p.read(func_set, len(expect_set))
    print('SetFeatureInt prologue match:', got == expect_set)
    if got != expect_set:
        print('prologue:', got.hex()); return

    # pick window 10, control id 1 (Button1)
    target = None
    for w in H.iter_object_chain(p, fileptr, 0x20):
        wid = H.fval(p, w, 20001)
        if wid and wid['int'] == 10:
            for c in H.iter_object_chain(p, w, 0x1C):
                cid = H.fval(p, c, 1000)
                if cid and cid['int'] == 1:
                    target = c; break
        if target: break
    if not target:
        print('target control not found'); return
    print('target control', hex(target), 'type', p.u32(target + 4))

    # geometry nodes
    node_x, orig_x = feat_int(p, target, 1003, 0)
    node_w, orig_w = feat_int(p, target, 1002, 0)
    orig_xd = feat_double(p, target, 1003, 0)
    orig_wd = feat_double(p, target, 1002, 0)
    _, orig_1081 = feat_int(p, target, 1081, 0)
    _, orig_1270 = feat_int(p, target, 1270, 0)
    print('originals: x=%s (d=%s) w=%s (d=%s) 1081=%s 1270=%s' %
          (orig_x, orig_xd, orig_w, orig_wd, orig_1081, orig_1270))
    if orig_x is None or orig_w is None:
        print('geometry features missing'); return

    results = {'pid': pid, 'target': hex(target), 'orig': {
        'x': orig_x, 'x_double': orig_xd, 'w': orig_w, 'w_double': orig_wd,
        'f1081': orig_1081, 'f1270': orig_1270}}

    restored = False
    try:
        # Test 1: set X = orig+7 via API
        rc = remote_call(p, func_set, target, [1003, 0, 0, orig_x + 7])
        _, x1 = feat_int(p, target, 1003, 0)
        xd1 = feat_double(p, target, 1003, 0)
        results['set_x'] = {'rc': rc, 'x': x1, 'x_double': xd1,
                            'ok': x1 == orig_x + 7 and abs(xd1 - (orig_x + 7)) < 1e-9}
        print('set X ->', results['set_x'])

        # restore X
        rc = remote_call(p, func_set, target, [1003, 0, 0, orig_x])
        _, x2 = feat_int(p, target, 1003, 0)
        results['restore_x'] = {'rc': rc, 'x': x2, 'ok': x2 == orig_x}
        print('restore X ->', results['restore_x'])

        # Test 2: set width = orig+5 (also triggers 1081)
        rc = remote_call(p, func_set, target, [1002, 0, 0, orig_w + 5])
        _, w1 = feat_int(p, target, 1002, 0)
        wd1 = feat_double(p, target, 1002, 0)
        _, f1081_after = feat_int(p, target, 1081, 0)
        results['set_w'] = {'rc': rc, 'w': w1, 'w_double': wd1, 'f1081': f1081_after,
                            'ok': w1 == orig_w + 5 and abs(wd1 - (orig_w + 5)) < 1e-9}
        print('set W ->', results['set_w'])

        # restore W and 1081
        rc = remote_call(p, func_set, target, [1002, 0, 0, orig_w])
        _, w2 = feat_int(p, target, 1002, 0)
        results['restore_w'] = {'rc': rc, 'w': w2, 'ok': w2 == orig_w}
        if orig_1081 is not None and f1081_after != orig_1081:
            rc = remote_call(p, func_set, target, [1081, 0, 0, orig_1081])
            _, f1081_rest = feat_int(p, target, 1081, 0)
            results['restore_f1081'] = {'rc': rc, 'value': f1081_rest,
                                        'ok': f1081_rest == orig_1081}
        print('restore W ->', results['restore_w'], 'restore 1081 ->', results.get('restore_f1081'))

        # Test 3: 1270 position lock
        if orig_1270 is not None and node_x is not None:
            rc = remote_call(p, func_set, target, [1270, 0, 0, 1])
            rc2 = remote_call(p, func_set, target, [1003, 0, 0, orig_x + 9])
            _, x_locked = feat_int(p, target, 1003, 0)
            results['lock_1270'] = {'set_lock_rc': rc, 'set_x_rc': rc2,
                                    'x_after': x_locked,
                                    'locked': x_locked == orig_x}
            print('1270 lock ->', results['lock_1270'])
            # restore lock and X
            remote_call(p, func_set, target, [1270, 0, 0, orig_1270])
            remote_call(p, func_set, target, [1003, 0, 0, orig_x])
        else:
            results['lock_1270'] = {'skipped': 'feature 1270 not present'}

        # final verification
        _, fx = feat_int(p, target, 1003, 0)
        _, fw = feat_int(p, target, 1002, 0)
        _, f1081f = feat_int(p, target, 1081, 0)
        _, f1270f = feat_int(p, target, 1270, 0)
        results['final'] = {'x': fx, 'w': fw, 'f1081': f1081f, 'f1270': f1270f,
                            'all_restored': (fx == orig_x and fw == orig_w and
                                             (orig_1081 is None or f1081f == orig_1081) and
                                             (orig_1270 is None or f1270f == orig_1270))}
        restored = results['final']['all_restored']
        print('final ->', results['final'])
    finally:
        if not restored:
            # best-effort restore
            try:
                remote_call(p, func_set, target, [1003, 0, 0, orig_x])
                remote_call(p, func_set, target, [1002, 0, 0, orig_w])
                if orig_1081 is not None:
                    remote_call(p, func_set, target, [1081, 0, 0, orig_1081])
                if orig_1270 is not None:
                    remote_call(p, func_set, target, [1270, 0, 0, orig_1270])
                print('best-effort restore executed')
            except Exception as ex:
                print('best-effort restore failed:', ex)
        outpath = os.path.join(TOOLS, 'hmi_write_test_last.json')
        with open(outpath, 'w', encoding='utf-8') as f:
            json.dump(results, f, ensure_ascii=False, indent=1)
        print('JSON ->', outpath)
        p.close()

if __name__ == '__main__':
    main()
