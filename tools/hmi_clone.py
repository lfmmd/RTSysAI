"""
净室克隆 TestPro HMI 的内存镜像：从 live RTSys 进程读出全部窗口/控件/特征，
再建一个全新的 scratch FileClass，逐对复刻几何/特征，验证插件能力可复现。
仅在内存里克隆，不落盘、不改动原项目。
"""
import ctypes, sys, struct, os, json, time, hashlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hmi_probe as H
import hmi_api_test as A

k32 = H.k32

def resolve_exports():
    rtsys_dir = os.environ.get('RTSYS_DIR', r'C:\Program Files\RTSys')
    dll_path = os.path.join(rtsys_dir, 'ExtMainHmi.dll')
    with open(dll_path, 'rb') as f:
        data=f.read()
    e_lfanew=struct.unpack('<I',data[0x3c:0x40])[0]
    opt_off=e_lfanew+24
    export_rva=struct.unpack('<I',data[opt_off+0x60:opt_off+0x64])[0]
    def rva_to_off(rva):
        num_sec=struct.unpack('<H',data[e_lfanew+6:e_lfanew+8])[0]
        size_opt=struct.unpack('<H',data[e_lfanew+20:e_lfanew+22])[0]
        sec_off=e_lfanew+24+size_opt
        for i in range(num_sec):
            base=sec_off+i*40
            vsize,va,rawsize,rawptr=struct.unpack('<IIII',data[base+8:base+24])
            if va<=rva<va+max(vsize,rawsize):
                return rawptr+(rva-va)
        return rva
    exp_off=rva_to_off(export_rva)
    nname=struct.unpack('<I',data[exp_off+24:exp_off+28])[0]
    addr_funcs=struct.unpack('<I',data[exp_off+28:exp_off+32])[0]
    addr_names=struct.unpack('<I',data[exp_off+32:exp_off+36])[0]
    addr_ords=struct.unpack('<I',data[exp_off+36:exp_off+40])[0]
    names={
      b'?SetFeatureInt@ZHmi_BaseObject@@UAEIHHHH@Z':None,
      b'?FindWindowW@ZHmi_FileClass@@QAEPAVZHmi_WindowClass@@H@Z':None,
      b'?FindControl@ZHmi_WindowClass@@QAEPAVZHmi_ControlClass@@H@Z':None,
      b'?ZHMI_GetEditObject@@YAPAVZHmi_BaseObject@@XZ':None,
    }
    out={}
    for i in range(nname):
        name_rva=struct.unpack('<I',data[rva_to_off(addr_names)+i*4:rva_to_off(addr_names)+i*4+4])[0]
        name_off=rva_to_off(name_rva)
        end=data.find(b'\x00',name_off)
        nm=data[name_off:end]
        if nm in names:
            ord_idx=struct.unpack('<H',data[rva_to_off(addr_ords)+i*2:rva_to_off(addr_ords)+i*2+2])[0]
            func_rva=struct.unpack('<I',data[rva_to_off(addr_funcs)+ord_idx*4:rva_to_off(addr_funcs)+ord_idx*4+4])[0]
            out[nm]=func_rva
    return out

def main():
    pid=H.find_pid('RTSys.exe')
    if not pid:
        print('RTSys.exe not running'); return
    p=H.Proc(pid)
    # reopen write + thread
    H.k32.CloseHandle(p.h)
    p.h=H.k32.OpenProcess(0x10|0x20|0x8|0x400, False, pid)
    if not p.h:
        print('open write failed'); return
    mods=H.enum_modules(pid)
    ext=mods['extmainhmi.dll'][0]
    R=lambda off: ext+off
    set_int=R(0x1D3E0)
    # 1. snapshot source
    edit=p.u32(ext+0x5DC2C)
    print('source file',hex(edit))
    wins=H.iter_object_chain(p,edit,0x20)
    print('source windows',len(wins))
    snapshot=[]
    for w in wins:
        wid=H.fval(p,w,20001)
        wname=H.fval(p,w,20003)
        win={'id': wid['int'] if wid else 0, 'name': wname['string'] if wname else None, 'controls':[]}
        # window geometry
        for main,sub in [(1002,0),(1002,1),(1003,0),(1003,1)]:
            v=H.fval(p,w,main,sub)
            win.setdefault('geo',{})[f'{main}[{sub}]']= v['int'] if v else None
        for c in H.iter_object_chain(p,w,0x1C):
            cid=H.fval(p,c,1000)
            cname=H.fval(p,c,1001)
            ctype=p.u32(c+4)
            geo={}
            for main,sub in [(1002,0),(1002,1),(1003,0),(1003,1)]:
                v=H.fval(p,c,main,sub)
                geo[f'{main}[{sub}]']= v['int'] if v else 0
            # feature count + sample of int feature ids
            feats=H.iter_feature_nodes(p,c)
            win['controls'].append({
                'id': cid['int'] if cid else 0,
                'type': ctype,
                'name': cname['string'] if cname else None,
                'geo': geo,
                'feature_count': len(feats),
            })
        print(f" WIN id={win['id']} name={win['name']!r} controls={len(win['controls'])}")
        snapshot.append(win)

    # 2. clone into scratch
    scratch=k32.VirtualAllocEx(p.h,None,0x800,0x3000,0x04)
    print('scratch',hex(scratch))
    A.remote_call(p,R(0x28A80),scratch,[])
    new_window=R(0x2B650); new_control=R(0x244C0)
    clone_map=[]  # (win_id, ctrl_id, src_addr, clone_addr)
    for winfo in snapshot:
        nw=A.remote_call(p,new_window,scratch,[winfo['id']])
        # window geometry
        for key,val in winfo['geo'].items():
            main=int(key.split('[')[0]); sub=int(key.split('[')[1].rstrip(']'))
            if val is not None:
                A.remote_call(p,set_int,nw,[main,sub,0,val])
        for cinfo in winfo['controls']:
            g=cinfo['geo']
            nc=A.remote_call(p,new_control,nw,[
                cinfo['id'], cinfo['type'],
                g['1003[0]'], g['1003[1]'],
                g['1002[0]'], g['1002[1]'],
            ])
            clone_map.append((winfo['id'],cinfo['id'],nc))
    print(f'cloned: {len(clone_map)} controls into {len(snapshot)} windows')

    # 3. verify clone by chain walk (like plugin's HmiInternal.EnumerateChain)
    clone_wins=H.iter_object_chain(p,scratch,0x20)
    print('clone windows chain:',len(clone_wins))
    mism=0; checked=0
    # index snapshot by (win,ctrl)
    snap_idx={}
    for wi in snapshot:
        for ci in wi['controls']:
            snap_idx[(wi['id'],ci['id'])]=(wi,ci)
    for cw in clone_wins:
        wid=H.fval(p,cw,20001)
        wid = wid['int'] if wid else None
        for cc in H.iter_object_chain(p,cw,0x1C):
            cid=H.fval(p,cc,1000)
            cid = cid['int'] if cid else None
            key=(wid,cid)
            if key not in snap_idx:
                print(' clone has extra control',key); mism+=1; continue
            wi,ci=snap_idx[key]
            for main,sub in [(1002,0),(1002,1),(1003,0),(1003,1)]:
                want=ci['geo'][f'{main}[{sub}]'] if main in (1002,1003) else None
                # geometry keys include both mains; compare
                want=ci['geo'][f'{main}[{sub}]']
                got=H.fval(p,cc,main,sub)
                got=got['int'] if got else None
                checked+=1
                if got!=want:
                    print(f' MISMATCH win{wid} ctl{cid} {main}[{sub}] want={want} got={got}')
                    mism+=1
    print(f'verify: checked={checked} geom values, mismatches={mism}')
    # 4. spot-check one clone control: move +30 then restore (combination action on clone)
    if clone_map:
        _,_,nc=clone_map[0]
        ox=H.fval(p,nc,1003,0)['int']
        A.remote_call(p,set_int,nc,[1003,0,0,ox+30])
        print('clone move +30:',ox,'->',H.fval(p,nc,1003,0)['int'])
        A.remote_call(p,set_int,nc,[1003,0,0,ox])
        print('clone restored:',H.fval(p,nc,1003,0)['int'])
    # 5. source untouched?
    src_edit=p.u32(ext+0x5DC2C)
    src_wins=H.iter_object_chain(p,src_edit,0x20)
    print('source windows after clone:',len(src_wins),'(expected 7)')
    # destroy clone
    A.remote_call(p,R(0x29A10),scratch,[])
    k32.VirtualFreeEx(p.h,ctypes.c_void_p(scratch),0,0x8000)
    print('clone destroyed')
    # save report
    report={'pid':pid,'windows':len(snapshot),'controls':sum(len(w['controls']) for w in snapshot),
            'checked':checked,'mismatches':mism}
    outpath=os.path.join(os.path.dirname(os.path.abspath(__file__)),'hmi_clone_last.json')
    with open(outpath,'w',encoding='utf-8') as f:
        json.dump(report,f,ensure_ascii=False,indent=1)
    print('JSON ->',outpath)
    p.close()

if __name__=='__main__':
    main()
