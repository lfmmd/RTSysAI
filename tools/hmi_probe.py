"""
Read-only in-process probe for RTSys HMI internals.
Uses ReadProcessMemory against the running RTSys.exe. No injection, no writes.
"""
import ctypes, struct, json, sys
from ctypes import wintypes as wt

k32 = ctypes.WinDLL('kernel32', use_last_error=True)

TH32CS_SNAPPROCESS = 0x00000002
TH32CS_SNAPMODULE = 0x00000008
TH32CS_SNAPMODULE32 = 0x00000010
PROCESS_VM_READ = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value

class PROCESSENTRY32(ctypes.Structure):
    _fields_ = [('dwSize', wt.DWORD), ('cntUsage', wt.DWORD), ('th32ProcessID', wt.DWORD),
                ('th32DefaultHeapID', ctypes.c_void_p), ('th32ModuleID', wt.DWORD),
                ('cntThreads', wt.DWORD), ('th32ParentProcessID', wt.DWORD),
                ('pcPriClassBase', ctypes.c_long), ('dwFlags', wt.DWORD),
                ('szExeFile', ctypes.c_char * 260)]

class MODULEENTRY32(ctypes.Structure):
    _fields_ = [('dwSize', wt.DWORD), ('th32ModuleID', wt.DWORD), ('th32ProcessID', wt.DWORD),
                ('GlblcntUsage', wt.DWORD), ('ProccntUsage', wt.DWORD),
                ('modBaseAddr', ctypes.c_void_p), ('modBaseSize', wt.DWORD),
                ('hModule', ctypes.c_void_p), ('szModule', ctypes.c_char * 256),
                ('szExePath', ctypes.c_char * 260)]

k32.CreateToolhelp32Snapshot.restype = ctypes.c_void_p
k32.CreateToolhelp32Snapshot.argtypes = [wt.DWORD, wt.DWORD]
k32.Process32First.argtypes = [ctypes.c_void_p, ctypes.POINTER(PROCESSENTRY32)]
k32.Process32Next.argtypes = [ctypes.c_void_p, ctypes.POINTER(PROCESSENTRY32)]
k32.Module32First.argtypes = [ctypes.c_void_p, ctypes.POINTER(MODULEENTRY32)]
k32.Module32Next.argtypes = [ctypes.c_void_p, ctypes.POINTER(MODULEENTRY32)]
k32.OpenProcess.restype = ctypes.c_void_p
k32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
k32.ReadProcessMemory.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
                                  ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
k32.CloseHandle.argtypes = [ctypes.c_void_p]
k32.VirtualQueryEx.restype = ctypes.c_size_t

MEM_COMMIT = 0x1000
PAGE_NOACCESS = 0x01
PAGE_GUARD = 0x100
PAGE_READABLE = (0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80)

class MBI(ctypes.Structure):
    _fields_ = [('BaseAddress', ctypes.c_void_p), ('AllocationBase', ctypes.c_void_p),
                ('AllocationProtect', wt.DWORD), ('RegionSize', ctypes.c_size_t),
                ('State', wt.DWORD), ('Protect', wt.DWORD), ('Type', wt.DWORD)]

k32.VirtualQueryEx.argtypes = [ctypes.c_void_p, ctypes.c_void_p,
                                ctypes.POINTER(MBI), ctypes.c_size_t]

def enum_regions(h):
    addr = 0
    mbi = MBI()
    while addr < 0x80000000:
        got = k32.VirtualQueryEx(h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi))
        if not got:
            break
        base = mbi.BaseAddress or 0
        size = mbi.RegionSize or 0
        if size == 0:
            break
        yield base, size, mbi.State, mbi.Protect, mbi.Type
        nxt = base + size
        if nxt <= addr:
            break
        addr = nxt

def scan_fileclass(p, ext_base, vtable_va, max_total=1200 * 1024 * 1024):
    """Scan readable private memory for ZHmi_FileClass objects (vtable at offset 0)."""
    pat = struct.pack('<I', vtable_va)
    hits = []
    scanned = 0
    for base, size, state, protect, typ in enum_regions(p.h):
        if state != MEM_COMMIT:
            continue
        if protect & (PAGE_NOACCESS | PAGE_GUARD):
            continue
        if not (protect & PAGE_READABLE):
            continue
        if size > 64 * 1024 * 1024:
            continue
        if scanned > max_total:
            break
        try:
            data = p.read(base, size)
        except OSError:
            continue
        scanned += size
        pos = 0
        while True:
            idx = data.find(pat, pos)
            if idx < 0:
                break
            if idx % 4 == 0:
                hits.append(base + idx)
            pos = idx + 1
    return hits, scanned

KNOWN_CONTROLS = {
    11:'Window',
    700:'Line/Polyline/Polygon',702:'Rect',706:'Bezier',701:'Ellipse/Arc/Sector',712:'Scale',
    645:'Table',707:'Import',705:'Import',708:'Import',709:'Import',710:'Import',711:'Import',
    703:'StaticText',704:'Picture',600:'BitState',601:'WordState',616:'Animation',723:'FlowBlock',
    630:'FlowBlock',631:'FlowBlock',602:'BitModify',603:'WordModify',604:'BitSwitch',605:'WordSwitch',
    609:'Button',608:'KeyButton',610:'KeyButton',612:'Value show',611:'String show',713:'String show',
    607:'Slip',717:'Report view',714:'File3Edit',900:'File3Edit',715:'CAD',606:'List',730:'Menu',
    721:'Tree',716:'File browser',733:'Group box',731:'Tab control',734:'Panel',621:'Timer',
    620:'Timer',640:'Timer',641:'Timer',901:'Custom',
    622:'Pie Chart',623:'Bar Chart',624:'Meter',625:'XY Chart',626:'Trend Chart',627:'Data Group Show',
    635:'Event Show',636:'Alarm Show',637:'Alarm Bar',719:'Progress Bar',720:'Dynamic Scale',
    722:'Scheduling',724:'Bar Code',725:'Radio Button',726:'Check Box',727:'Message Out',
    728:'LayDate',736:'Basic Extend',
}

def find_pid(exe_name):
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snap == INVALID_HANDLE_VALUE:
        return None
    pe = PROCESSENTRY32(); pe.dwSize = ctypes.sizeof(pe)
    ok = k32.Process32First(snap, ctypes.byref(pe))
    found = None
    while ok:
        name = pe.szExeFile.decode('mbcs', 'replace')
        if name.lower() == exe_name.lower():
            found = pe.th32ProcessID
            break
        ok = k32.Process32Next(snap, ctypes.byref(pe))
    k32.CloseHandle(snap)
    return found

def enum_modules(pid):
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid)
    if snap == INVALID_HANDLE_VALUE:
        return {}
    me = MODULEENTRY32(); me.dwSize = ctypes.sizeof(me)
    mods = {}
    ok = k32.Module32First(snap, ctypes.byref(me))
    while ok:
        name = me.szModule.decode('mbcs', 'replace')
        mods[name.lower()] = (me.modBaseAddr or 0, me.modBaseSize)
        ok = k32.Module32Next(snap, ctypes.byref(me))
    k32.CloseHandle(snap)
    return mods

class Proc:
    def __init__(self, pid):
        self.pid = pid
        self.h = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
        if not self.h:
            self.h = k32.OpenProcess(PROCESS_VM_READ, False, pid)
        if not self.h:
            raise OSError(ctypes.get_last_error(), 'OpenProcess failed')
    def read(self, addr, n):
        buf = ctypes.create_string_buffer(n)
        got = ctypes.c_size_t(0)
        if not k32.ReadProcessMemory(self.h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got)):
            raise OSError(ctypes.get_last_error(), 'RPM failed at 0x%X' % addr)
        return buf.raw[:got.value]
    def u32(self, addr):
        return struct.unpack('<I', self.read(addr, 4))[0]
    def i32(self, addr):
        return struct.unpack('<i', self.read(addr, 4))[0]
    def u16(self, addr):
        return struct.unpack('<H', self.read(addr, 2))[0]
    def f64(self, addr):
        return struct.unpack('<d', self.read(addr, 8))[0]
    def wstr(self, addr, maxchars=256):
        try:
            raw = self.read(addr, maxchars * 2)
        except OSError:
            return None
        s = raw.decode('utf-16-le', 'ignore')
        i = s.find('\x00')
        if i >= 0:
            s = s[:i]
        s = s.strip()
        if not s:
            return None
        for ch in s:
            o = ord(ch)
            if o < 0x20 or 0x7F <= o <= 0x9F or 0xD800 <= o <= 0xDFFF or 0xE000 <= o <= 0xF8FF:
                return None
        return s
    def close(self):
        try:
            k32.CloseHandle(self.h)
        except Exception:
            pass

def is_plausible(ptr):
    return ptr is not None and 0x10000 <= ptr < 0x80000000

def read_cstring_inline(p, holder_addr):
    """CString object stored inline: holder_addr+0 == m_pchData."""
    try:
        pdata = p.u32(holder_addr)
    except OSError:
        return None
    if not is_plausible(pdata):
        return None
    return p.wstr(pdata)

def read_cstring_ptr(p, holder_addr):
    """holder_addr holds a pointer to a CString object; object+0 == m_pchData."""
    try:
        obj = p.u32(holder_addr)
        if not is_plausible(obj):
            return None
        pdata = p.u32(obj)
    except OSError:
        return None
    if not is_plausible(pdata):
        return None
    return p.wstr(pdata)

def iter_feature_nodes(p, obj):
    """Replicates ZHmi_BaseObject::FindFeature list traversal."""
    try:
        head = p.u32(obj + 0x18)
    except OSError:
        return []
    if not head:
        return []
    try:
        sentinel_flag = p.u32(head + 24)
    except OSError:
        return []
    if sentinel_flag:
        start = p.u32(head + 4)
        if not start or start == head:
            return []
    else:
        start = head
    nodes = []
    seen = set()
    node = start
    for _ in range(4000):
        if node in seen or node == 0:
            break
        seen.add(node)
        nodes.append(node)
        try:
            nxt = p.u32(node + 8)
        except OSError:
            break
        if nxt == start or nxt == 0:
            break
        node = nxt
    return nodes

def iter_object_chain(p, holder, head_off):
    """Window list: holder+0x20 head, next at obj+0x0C (node ptr, minus 4)."""
    try:
        head = p.u32(holder + head_off)
    except OSError:
        return []
    if not head:
        return []
    res = []
    seen = set()
    cur = head
    for _ in range(4000):
        if cur in seen or cur == 0:
            break
        seen.add(cur)
        res.append(cur)
        try:
            nxt = p.u32(cur + 0x0C)
        except OSError:
            break
        if not nxt:
            break
        nxt -= 4
        if nxt == head:
            break
        cur = nxt
    return res

def feature_entry(p, node):
    try:
        main = p.i32(node)
        sub = p.read(node + 0x11, 1)[0]
        iv = p.i32(node + 0x20)
        dv = p.f64(node + 0x28)
        sv = read_cstring_ptr(p, node + 0x30)
        return {'main': main, 'sub': sub, 'int': iv, 'double': dv, 'string': sv}
    except OSError:
        return None

def find_feature(p, obj, main, sub=0):
    for n in iter_feature_nodes(p, obj):
        try:
            if p.i32(n) == main and p.read(n + 0x11, 1)[0] == sub:
                return n
        except OSError:
            continue
    return None

def fval(p, obj, main, sub=0):
    n = find_feature(p, obj, main, sub)
    if n is None:
        return None
    e = feature_entry(p, n)
    return None if e is None else e

def main():
    try:
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass
    pid = find_pid('RTSys.exe')
    if not pid:
        print(json.dumps({'ok': False, 'error': 'RTSys.exe not running'}, ensure_ascii=False))
        return
    try:
        p = Proc(pid)
    except OSError as ex:
        print(json.dumps({'ok': False, 'error': 'cannot open RTSys.exe: %s' % ex,
                          'pid': pid}, ensure_ascii=False))
        return
    mods = enum_modules(pid)
    ext = mods.get('extmainhmi.dll')
    if not ext:
        print(json.dumps({'ok': False, 'error': 'ExtMainHmi.dll not loaded', 'pid': pid}, ensure_ascii=False))
        return
    ext_base = ext[0]
    out = {'ok': True, 'pid': pid, 'extmainhmi_base': hex(ext_base),
           'modules': {k: [hex(v[0]), v[1]] for k, v in mods.items() if k in
                       ('rtsys.exe', 'extmainhmi.dll', 'exthmicontrol_w.dll', 'zmotion.dll')}}

    # ---- feature registry ----
    try:
        fb = p.u32(ext_base + 0x15DCB8)
        fe = p.u32(ext_base + 0x15DCBC)
        count = (fe - fb) // 152 if fe > fb else 0
        entries = []
        for i in range(min(count, 1000)):
            e = fb + 152 * i
            try:
                mid = p.i32(e)
                sub = p.read(e + 4, 1)[0]
                typ = p.i32(e + 8)
                name = read_cstring_inline(p, e + 148)
                entries.append({'id': mid, 'sub': sub, 'type': typ, 'name': name})
            except OSError:
                break
        out['feature_registry'] = {'begin': hex(fb), 'end': hex(fe), 'count': count,
                                   'entries': entries}
    except OSError as ex:
        out['feature_registry'] = {'error': str(ex)}

    # ---- control registry ----
    try:
        cb = p.u32(ext_base + 0x15DCEC)
        ce = p.u32(ext_base + 0x15DCF0)
        ccount = (ce - cb) // 168 if ce > cb else 0
        centries = []
        for i in range(min(ccount, 300)):
            e = cb + 168 * i
            try:
                tid = p.i32(e + 4)
                centries.append({'type': tid, 'name': KNOWN_CONTROLS.get(tid)})
            except OSError:
                break
        out['control_registry'] = {'begin': hex(cb), 'end': hex(ce), 'count': ccount,
                                   'entries': centries}
    except OSError as ex:
        out['control_registry'] = {'error': str(ex)}

    # ---- host project file table (RTSys.exe globals) ----
    rtsys = mods.get('rtsys.exe')
    if rtsys:
        rb = rtsys[0]
        try:
            arr = p.u32(rb + 0x4268AC)
            cnt = p.u32(rb + 0x4268B0)
            files = []
            if is_plausible(arr) and 0 < cnt < 500:
                for i in range(cnt):
                    e = arr + 76 * i
                    pp = p.u32(e)
                    s = p.wstr(pp, 260) if is_plausible(pp) else None
                    if s:
                        files.append(s)
            out['project_files'] = {'array': hex(arr), 'count': cnt, 'files': files}
        except OSError as ex:
            out['project_files'] = {'error': str(ex)}

    # ---- current edit object -> file ----
    fileptr = None
    try:
        edit = p.u32(ext_base + 0x5DC2C)
        out['edit_object'] = hex(edit) if edit else None
        if edit:
            fp = p.u32(edit + 0x10)
            if is_plausible(fp):
                fileptr = fp
    except OSError as ex:
        out['edit_object_error'] = str(ex)

    # ---- fallback: scan for ZHmi_FileClass vtable (no selection needed) ----
    import os
    if not fileptr and os.environ.get('HMI_PROBE_SKIP_SCAN') != '1':
        vtable_va = ext_base + 0x4973C
        try:
            hits, scanned = scan_fileclass(p, ext_base, vtable_va)
            cands = []
            for a in hits:
                try:
                    head = p.u32(a + 0x20)
                    if head and not is_plausible(head):
                        continue
                    wins = iter_object_chain(p, a, 0x20) if head else []
                    ok = True
                    for w in wins[:3]:
                        nodes = iter_feature_nodes(p, w)
                        if not nodes:
                            ok = False
                            break
                        mid = p.i32(nodes[0])
                        if not (1000 <= mid <= 30000):
                            ok = False
                            break
                    if ok:
                        cands.append((len(wins), a))
                except OSError:
                    continue
            cands.sort(reverse=True)
            out['fileclass_scan'] = {'vtable': hex(vtable_va), 'hits': len(hits),
                                     'scanned_bytes': scanned,
                                     'candidates': [{'addr': hex(a), 'windows': n} for n, a in cands[:10]]}
            if cands:
                fileptr = cands[0][1]
                out['file_object_source'] = 'vtable_scan'
        except Exception as ex:
            out['fileclass_scan'] = {'error': str(ex)}

    out['file_object'] = hex(fileptr) if fileptr else None
    if fileptr:
        try:
            wins = iter_object_chain(p, fileptr, 0x20)
            out['window_count'] = len(wins)
            win_list = []
            for w in wins[:64]:
                wid = fval(p, w, 20001)
                wname = fval(p, w, 20003)
                geo = {}
                for main in (1002, 1003):
                    for sub in (0, 1):
                        v = fval(p, w, main, sub)
                        geo['%d[%d]' % (main, sub)] = None if v is None else v['int']
                ctrls = iter_object_chain(p, w, 0x1C)
                clist = []
                for c in ctrls[:256]:
                    cid = fval(p, c, 1000)
                    cname = fval(p, c, 1001)
                    cgeo = {}
                    for main in (1002, 1003):
                        for sub in (0, 1):
                            v = fval(p, c, main, sub)
                            cgeo['%d[%d]' % (main, sub)] = None if v is None else v['int']
                    fcount = len(iter_feature_nodes(p, c))
                    try:
                        ctype = p.u32(c + 4)
                    except OSError:
                        ctype = None
                    clist.append({'id': None if cid is None else cid['int'],
                                  'type': ctype,
                                  'typeName': KNOWN_CONTROLS.get(ctype),
                                  'name': None if cname is None else cname['string'],
                                  'geo': cgeo, 'feature_count': fcount})
                try:
                    wtype = p.u32(w + 4)
                except OSError:
                    wtype = None
                win_list.append({'id': None if wid is None else wid['int'],
                                 'type': wtype,
                                 'name': None if wname is None else wname['string'],
                                 'geo': geo, 'control_count': len(ctrls),
                                 'controls': clist})
            out['windows'] = win_list
        except OSError as ex:
            out['windows_error'] = str(ex)

    import os
    outpath = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'hmi_probe_last.json')
    with open(outpath, 'w', encoding='utf-8') as f:
        json.dump(out, f, ensure_ascii=False, indent=1)
    # compact console summary (ASCII-safe)
    def n(d, k):
        v = d.get(k) if isinstance(d, dict) else None
        return v if v is not None else '?'
    print('PID', out.get('pid'), 'ExtMainHmi', out.get('extmainhmi_base'))
    fr = out.get('feature_registry', {})
    cr = out.get('control_registry', {})
    print('feature_registry count=', n(fr, 'count'), 'read=', len(fr.get('entries') or []))
    if fr.get('entries'):
        print('  first:', [(e['id'], e['sub'], e['type'], e['name']) for e in fr['entries'][:5]])
    print('control_registry count=', n(cr, 'count'), 'read=', len(cr.get('entries') or []))
    if cr.get('entries'):
        print('  types:', [e['type'] for e in cr['entries'][:60]])
    pf = out.get('project_files', {})
    print('project_files count=', pf.get('count'), 'files=', pf.get('files'))
    print('edit_object=', out.get('edit_object'), 'file_object=', out.get('file_object'))
    print('window_count=', n(out, 'window_count'))
    for w in (out.get('windows') or [])[:10]:
        print('  WIN', w.get('id'), repr(w.get('name')), w.get('geo'), 'controls=', w.get('control_count'))
        for c in (w.get('controls') or [])[:5]:
            print('    CTL', c.get('id'), repr(c.get('name')), c.get('geo'), 'features=', c.get('feature_count'))
    print('JSON ->', outpath)
    p.close()

if __name__ == '__main__':
    main()
