"""
Self-contained in-process API test for RTSys HMI internals.

Creates a scratch ZHmi_FileClass in the target process (NOT attached to any
document), adds a window and a Button control, exercises SetFeatureInt /
GetFeatureInt / FindFeature / SetString, verifies by reading memory, then
destroys the scratch object. No project object and no file is touched.
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
PAGE_READWRITE = 0x04
MEM_RELEASE = 0x8000

def remote_call(p, func, this, args):
    mem = k32.VirtualAllocEx(p.h, None, 4096, MEM_COMMIT_RESERVE, PAGE_EXECUTE_READWRITE)
    if not mem:
        raise OSError(ctypes.get_last_error(), 'VirtualAllocEx failed')
    result_addr = mem + 0x200
    code = bytearray()
    code += b'\xB8' + struct.pack('<I', this & 0xFFFFFFFF)
    for a in reversed(args):
        code += b'\x68' + struct.pack('<I', a & 0xFFFFFFFF)
    code += b'\x8B\xC8'
    code += b'\xB8' + struct.pack('<I', func & 0xFFFFFFFF)
    code += b'\xFF\xD0'
    code += b'\xA3' + struct.pack('<I', result_addr)
    code += b'\xC3'
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

def remote_write(p, addr, data):
    written = ctypes.c_size_t(0)
    buf = ctypes.create_string_buffer(data)
    if not k32.WriteProcessMemory(p.h, ctypes.c_void_p(addr), buf, len(data), ctypes.byref(written)):
        raise OSError(ctypes.get_last_error(), 'WriteProcessMemory data failed')

def main():
    try:
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass
    pid = H.find_pid('RTSys.exe')
    if not pid:
        print('RTSys.exe not running'); return
    p = H.Proc(pid)
    # reopen with write + remote-thread rights
    H.k32.CloseHandle(p.h)
    p.h = H.k32.OpenProcess(0x0010 | 0x0020 | 0x0008 | 0x0400, False, pid)
    if not p.h:
        print('cannot open RTSys.exe for write'); return
    ext = H.enum_modules(pid)['extmainhmi.dll'][0]
    R = lambda off: ext + off
    funcs = {
        'ctor': R(0x28A80), 'dtor': R(0x29A10),
        'new_window': R(0x2B650), 'new_control': R(0x244C0),
        'set_int': R(0x1D3E0), 'get_int': R(0xEB30),
        'find_feature': R(0xE700), 'set_string': R(0x5010),
    }
    prologues = {
        'ctor': '558bec6aff68',
        'dtor': '538bd956c703',
        'new_window': '558bec5356578bd9536a',
        'new_control': '558bec56578bf9ff7710',
        'set_int': '558bec568b750857ff75',
        'get_int': '558becff7510ff750cff',
        'find_feature': '558bec8b5118565785d2',
        'set_string': '558bec568bf18b463085',
    }
    ok = True
    for k, hexs in prologues.items():
        got = p.read(funcs[k], len(hexs) // 2)
        if got.hex() != hexs:
            print('PROLOGUE MISMATCH', k, got.hex()); ok = False
    if not ok:
        return
    print('all prologues match')

    scratch = k32.VirtualAllocEx(p.h, None, 0x200, MEM_COMMIT_RESERVE, PAGE_READWRITE)
    if not scratch:
        print('scratch alloc failed'); return
    results = {'pid': pid, 'scratch': hex(scratch)}
    try:
        # construct scratch FileClass
        ret = remote_call(p, funcs['ctor'], scratch, [])
        vt = p.u32(scratch)
        results['ctor'] = {'ret': hex(ret), 'vtable': hex(vt),
                           'ok': vt == R(0x4973C)}
        print('ctor ->', results['ctor'])

        # create window 1234
        win = remote_call(p, funcs['new_window'], scratch, [1234])
        results['new_window'] = {'win': hex(win), 'type': p.u32(win + 4) if win else None,
                                 'ok': win != 0 and p.u32(win + 4) == 11}
        print('window ->', results['new_window'])

        # create Button (609) id=1 at (10,20) 100x40
        ctrl = remote_call(p, funcs['new_control'], win, [1, 609, 10, 20, 100, 40])
        results['new_control'] = {'ctrl': hex(ctrl),
                                  'type': p.u32(ctrl + 4) if ctrl else None,
                                  'ok': ctrl != 0 and p.u32(ctrl + 4) == 609}
        print('control ->', results['new_control'])
        if not ctrl:
            return

        def fi(main, sub=0):
            v = H.fval(p, ctrl, main, sub)
            return None if v is None else v['int']
        def fd(main, sub=0):
            n = H.find_feature(p, ctrl, main, sub)
            return None if n is None else p.f64(n + 0x28)

        geom = {'id': fi(1000), 'w': fi(1002, 0), 'h': fi(1002, 1),
                'x': fi(1003, 0), 'y': fi(1003, 1)}
        results['initial_geometry'] = geom
        print('initial geometry ->', geom)

        # API read: GetFeatureInt(1003,0,0)
        got = remote_call(p, funcs['get_int'], ctrl, [1003, 0, 0])
        results['get_int'] = {'value': got, 'expected': geom['x'], 'ok': got == geom['x']}
        print('GetFeatureInt ->', results['get_int'])

        # API write: X = 77
        rc = remote_call(p, funcs['set_int'], ctrl, [1003, 0, 0, 77])
        results['set_x'] = {'rc': rc, 'x': fi(1003, 0), 'x_double': fd(1003, 0),
                            'ok': fi(1003, 0) == 77 and abs(fd(1003, 0) - 77.0) < 1e-9}
        print('SetFeatureInt(X) ->', results['set_x'])

        # API write: W = 150, check 1081 repaint flag
        before1081 = fi(1081)
        rc = remote_call(p, funcs['set_int'], ctrl, [1002, 0, 0, 150])
        results['set_w'] = {'rc': rc, 'w': fi(1002, 0), 'w_double': fd(1002, 0),
                            'f1081_before': before1081, 'f1081_after': fi(1081),
                            'ok': fi(1002, 0) == 150 and abs(fd(1002, 0) - 150.0) < 1e-9}
        print('SetFeatureInt(W) ->', results['set_w'])

        # 1270 lock test
        lock_before = fi(1270)
        remote_call(p, funcs['set_int'], ctrl, [1270, 0, 0, 1])
        remote_call(p, funcs['set_int'], ctrl, [1003, 0, 0, 99])
        locked_val = fi(1003, 0)
        remote_call(p, funcs['set_int'], ctrl, [1270, 0, 0, lock_before if lock_before is not None else 0])
        remote_call(p, funcs['set_int'], ctrl, [1003, 0, 0, 77])
        results['lock_1270'] = {'lock_before': lock_before, 'x_during_lock': locked_val,
                                'locked': locked_val == 77}
        print('1270 lock ->', results['lock_1270'])

        # string write: name = "HelloAI"
        feat = remote_call(p, funcs['find_feature'], ctrl, [1001, 0, 0])
        results['find_feature_1001'] = {'feat': hex(feat) if feat else None}
        if feat:
            str_addr = scratch + 0x180
            remote_write(p, str_addr, b'HelloAI\x00')
            remote_call(p, funcs['set_string'], feat, [str_addr])
            name = H.read_cstring_ptr(p, feat + 0x30)
            results['set_string'] = {'name': name, 'ok': name == 'HelloAI'}
            print('SetString ->', results['set_string'])
        else:
            results['set_string'] = {'skipped': 'feature 1001 not found on new control'}

        results['final_geometry'] = {'w': fi(1002, 0), 'h': fi(1002, 1),
                                     'x': fi(1003, 0), 'y': fi(1003, 1)}
        print('final geometry ->', results['final_geometry'])
    finally:
        try:
            remote_call(p, funcs['dtor'], scratch, [])
            print('scratch destroyed')
            results['destroyed'] = True
        except Exception as ex:
            print('destroy failed:', ex)
            results['destroyed'] = False
        k32.VirtualFreeEx(p.h, ctypes.c_void_p(scratch), 0, MEM_RELEASE)
        outpath = os.path.join(TOOLS, 'hmi_api_test_last.json')
        with open(outpath, 'w', encoding='utf-8') as f:
            json.dump(results, f, ensure_ascii=False, indent=1)
        print('JSON ->', outpath)
        p.close()

if __name__ == '__main__':
    main()
