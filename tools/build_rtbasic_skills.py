#!/usr/bin/env python3
"""从 RTSys 本体自带的 RTBasic 帮助(CHM 解包目录)收集命令参考库。

源:  %USERPROFILE%\\Desktop\\RTSys\\help\\RTBasic\\
     doc.hhc (gb18030, "命令名 -- 中文说明" -> N.html) + 编号 HTML 页
输出: PlugInWorker/skills/rtbasic/zh/
     index.json  {"name","type","desc","file","sig"} — 与 AiSkills.LoadIndex 消费格式一致
     N.html      转存 UTF-8(去图片目录依赖,只拷索引引用到的页)

用法:
    python tools/build_rtbasic_skills.py [--src <RTBasic帮助目录>] [--out <输出目录>]
"""
import argparse
import html as html_mod
import json
import os
import re
import shutil
import sys
from pathlib import Path

# 每行结构: <td>标签</td> <td>内容</td>;标签是 类型/描述/语法
_RE_CELL = r'>{}<.*?</td>\s*<td[^>]*>(.*?)</td>'
_RE_TYPE = re.compile(_RE_CELL.format('类型'), re.S)
_RE_DESC = re.compile(_RE_CELL.format('描述'), re.S)
_RE_SIG = re.compile(_RE_CELL.format('语法'), re.S)
# 部分页（常数 / 系统参数）没有“描述/语法”行，改用 名字/说明/值/范围/单位/初始值/权限/相关指令
_RE_NAME = re.compile(_RE_CELL.format('名字'), re.S)
_RE_NOTE = re.compile(_RE_CELL.format('说明'), re.S)
_RE_VALUE = re.compile(_RE_CELL.format('值'), re.S)
_RE_RANGE = re.compile(_RE_CELL.format('范围'), re.S)
_RE_UNIT = re.compile(_RE_CELL.format('单位'), re.S)
_RE_DEFAULT = re.compile(_RE_CELL.format('初始值'), re.S)
_RE_PERM = re.compile(_RE_CELL.format('权限'), re.S)
_RE_RELATED = re.compile(_RE_CELL.format('相关指令'), re.S)
_RE_TITLE = re.compile(r'<title>(.*?)</title>', re.S)
_RE_META_CHARSET = re.compile(
    r'(<meta[^>]*charset=)(gb2312|gbk|gb18030)([^>]*>)', re.I)
_RE_TAG = re.compile(r'<[^>]+>')
_RE_WS = re.compile(r'\s+')


def parse_hhc(hhc_path: Path):
    """doc.hhc -> [(name, local_html)]，按文档顺序。gb18030 解码。

    该文件的每个条目固定为 Name/Local(可选 ImageNumber) 三个 param 顺序排列，
    故分别抓取两个列表后按序配对。
    """
    text = hhc_path.read_bytes().decode('gb18030', errors='replace')
    names = re.findall(r'name="Name"\s+value="([^"]*)"', text, re.I)
    locals_ = re.findall(r'name="Local"\s+value="([^"]*)"', text, re.I)
    if len(names) != len(locals_):
        sys.exit(f'[错误] doc.hhc Name({len(names)})/Local({len(locals_)}) 数量不一致')
    return list(zip(names, locals_))


def clean_text(fragment: str, limit: int) -> str:
    """HTML 单元格 -> 纯文本：剥标签、解实体、压空白、截断。"""
    text = _RE_TAG.sub('', fragment)
    text = html_mod.unescape(text)
    text = text.replace('\xa0', ' ')
    text = _RE_WS.sub(' ', text).strip()
    if len(text) > limit:
        text = text[:limit - 1] + '…'
    return text


def _cell(regex, text: str, limit: int) -> str:
    m = regex.search(text)
    return clean_text(m.group(1), limit) if m else ''


def sys_param_signature(pname: str, typ: str, perm: str) -> str:
    """系统参数 -> 读写函数签名（按类型选 Str/Dbl/Int，按权限决定读/写方向）。"""
    # “名字”单元格可能是 `"NameList" 或 ""`，签名只取第一个带引号的参数名。
    m = re.search(r'"([^"]+)"', pname)
    if m:
        pname = f'"{m.group(1)}"'
    if '字符串' in typ:
        fn = 'Str'
    elif '浮点' in typ:
        fn = 'Dbl'
    elif '整数' in typ or '整型' in typ:
        fn = 'Int'
    else:
        return f'参数 {pname}'
    if '只读' in perm:
        return f'value = ZV_SysGet{fn}({pname})'
    if '只写' in perm:
        return f'ZV_SysSet{fn}({pname}, value)'
    return f'ZV_SysSet{fn}({pname}, value) / value = ZV_SysGet{fn}({pname})'


def parse_page(html_path: Path):
    """单页 -> (type, desc, sig, meta, raw)；页面缺失/无表格返回 None。

    meta 收集没有“描述/语法”行时的替代信息（常数取值、系统参数属性），
    由 main 结合命令名合成 desc/sig。
    """
    try:
        raw = html_path.read_bytes()
    except OSError:
        return None
    text = raw.decode('gb18030', errors='replace')

    typ = _cell(_RE_TYPE, text, 40)
    desc = _cell(_RE_DESC, text, 160)
    sig = _cell(_RE_SIG, text, 200)
    meta = {}
    if not desc:
        note = _cell(_RE_NOTE, text, 160)
        if note:
            meta['note'] = note
    if not sig:
        value = _cell(_RE_VALUE, text, 40)
        if value:
            meta['value'] = value
        pname = _cell(_RE_NAME, text, 40)
        if pname:
            meta['pname'] = pname
            meta['range'] = _cell(_RE_RANGE, text, 40)
            meta['unit'] = _cell(_RE_UNIT, text, 20)
            meta['default'] = _cell(_RE_DEFAULT, text, 40)
            meta['perm'] = _cell(_RE_PERM, text, 20)
    related = _cell(_RE_RELATED, text, 80)
    if related:
        meta['related'] = related
    if not (typ or desc or sig or meta):
        return None
    return typ, desc, sig, meta, raw


# "名 -- 说明" 分隔符：绝大多数是 "--"，少数页面用 en-dash "–" 且两侧空格可有可无
_RE_SEP = re.compile(r'\s*[-–—]{1,2}\s*')


def split_names(raw_name: str):
    """"MOVESPLINE / MOVESPLINEABS -- 样条插补" 的斜杠并列名拆成多条目。"""
    head = _RE_SEP.split(raw_name, maxsplit=1)[0].strip()
    if '/' in head:
        parts = [p.strip() for p in head.split('/') if p.strip()]
        if len(parts) > 1:
            return parts
    return [head or raw_name]


def is_command_entry(raw_name: str) -> bool:
    return bool(_RE_SEP.search(raw_name))


def main():
    repo = Path(__file__).resolve().parent.parent
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', default=os.path.expandvars(r'%USERPROFILE%\Desktop\RTSys\help\RTBasic'))
    ap.add_argument('--out', default=str(repo / 'PlugInWorker' / 'skills' / 'rtbasic' / 'zh'))
    args = ap.parse_args()

    src, out = Path(args.src), Path(args.out)
    hhc = src / 'doc.hhc'
    if not hhc.exists():
        sys.exit(f'[错误] 找不到 {hhc}')

    entries = parse_hhc(hhc)
    print(f'doc.hhc 条目: {len(entries)}')

    index, seen, skipped_no_cmd, skipped_no_page = [], set(), [], []
    for name, local in entries:
        name = name.strip()
        if not is_command_entry(name):   # 章节页/目录页，不进命令索引
            skipped_no_cmd.append(name)
            continue
        page = src / local
        parsed = parse_page(page)
        if parsed is None:
            skipped_no_page.append(f'{name} ({local})')
            continue
        typ, desc, sig, meta, raw = parsed
        for cmd in split_names(name):
            key = cmd.upper()
            if key in seen:           # 重名去重，先到先得
                continue
            seen.add(key)

            # 无“语法”行：常数补“NAME = 值”，系统参数补 ZV_SysSet/Get 读写签名。
            # 系统参数页也可能有“值”行（只读参数的当前值），优先按参数处理。
            cmd_sig = sig
            if not cmd_sig and meta.get('pname'):
                cmd_sig = sys_param_signature(meta['pname'], typ, meta.get('perm', ''))
            elif not cmd_sig and meta.get('value'):
                cmd_sig = f'{cmd} = {meta["value"]}'

            # 无“描述”行：用“说明”+ 参数属性合成一条摘要。
            cmd_desc = desc
            if not cmd_desc:
                parts = []
                if meta.get('note'):
                    parts.append(meta['note'])
                if meta.get('default'):
                    parts.append('初始值 ' + meta['default'])
                if meta.get('range'):
                    parts.append('范围 ' + meta['range'])
                if meta.get('unit'):
                    parts.append('单位 ' + meta['unit'])
                if meta.get('perm'):
                    parts.append('权限 ' + meta['perm'])
                if meta.get('pname') and meta.get('value'):
                    parts.append('值 ' + meta['value'])
                if not parts and meta.get('related'):
                    parts.append('相关：' + meta['related'])
                cmd_desc = '；'.join(parts)
                if len(cmd_desc) > 160:
                    cmd_desc = cmd_desc[:159] + '…'

            # 无语法条目（结构关键字等）把“相关指令”补进摘要；有空间才补，避免截断正文。
            if cmd_desc and not cmd_sig and meta.get('related') \
                    and len(cmd_desc) + len(meta['related']) + 4 <= 160:
                cmd_desc += '；相关：' + meta['related']

            # 仍然没有说明（如常数页只有“值”行）→ 用 doc.hhc 的“NAME -- 短说明”。
            if not cmd_desc:
                head = _RE_SEP.split(name, maxsplit=1)
                if len(head) == 2 and head[1].strip():
                    cmd_desc = head[1].strip()[:160]

            index.append({'name': cmd, 'type': typ, 'desc': cmd_desc,
                          'file': local, 'sig': cmd_sig})
        # 转存 UTF-8：只改 charset 声明，正文不动（读取侧 CleanHelpHtml 会剥 head/样式/img）
        dest_html = out / local
        if not dest_html.exists():
            out.mkdir(parents=True, exist_ok=True)
            fixed = _RE_META_CHARSET.sub(r'\1utf-8\3', raw.decode('gb18030', errors='replace'), count=1)
            dest_html.write_text(fixed, encoding='utf-8')

    out.mkdir(parents=True, exist_ok=True)
    with open(out / 'index.json', 'w', encoding='utf-8') as f:
        json.dump(index, f, ensure_ascii=False, indent=1)

    print(f'索引条目: {len(index)} (去重后)')
    print(f'跳过非命令页: {len(skipped_no_cmd)}  跳过缺页/无表格: {len(skipped_no_page)}')
    if skipped_no_cmd:
        print('  非命令页样例:', '; '.join(skipped_no_cmd[:8]))
    if skipped_no_page:
        print('  缺页样例:', '; '.join(skipped_no_page[:8]))
    for e in index:
        if e['name'] in ('MOVE', 'UNITS', 'ADDAX'):
            print('  抽查:', json.dumps(e, ensure_ascii=False)[:180])
    print(f'输出: {out}\\index.json + {len(seen)} 个命令页')


if __name__ == '__main__':
    main()
