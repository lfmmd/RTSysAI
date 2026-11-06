#!/usr/bin/env python3
"""校验提示词/工具描述里引用的 RTBasic 标识符是否真实存在于参考库。

背景: RTBasic 是私有方言,提示词里写错命令(SQRT/STR/WAITS/IN_POS ...)会直接
误导 AI 生成编译不过的代码。本脚本扫描所有面向 LLM 的文本:

  1) 代码上下文提取 —— 反引号内的代码块、`WORD(...)` 形态的调用、行首大写赋值左侧
     这几处的标识符必须能在 skills/rtbasic/zh/index.json 里查到。
  2) 方言污染黑名单 —— TrioBASIC/IEC 遗留词(SQRT/STR/WAITS/IN_POS/DIM..AS ...)
     一旦出现即报错,这类词最容易伪装成 RTBasic 指令。

用法:
    python tools/check_rtbasic_refs.py            # 扫描默认文件集
    python tools/check_rtbasic_refs.py --verbose  # 额外打印命中统计
"""
import argparse
import json
import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
INDEX = REPO / 'PlugInWorker' / 'skills' / 'rtbasic' / 'zh' / 'index.json'

# 扫描目标: 面向 LLM 的文本 (提示词 / skill / 工具描述 / 子代理提示)
TARGETS = [
    REPO / 'PlugInWorker' / 'skills' / 'prompts' / 'rtbasic.md',
    REPO / 'PlugInWorker' / 'skills' / 'general' / 'safe-coding' / 'SKILL.md',
    REPO / 'PlugInWorker' / 'AiPrompt.cs',
    REPO / 'PlugInWorker' / 'AiSubagent.cs',
    REPO / 'PlugInWorker' / 'AiTools.cs',
]

# 方言污染黑名单: 这些是 TrioBASIC/IEC/其它 BASIC 的写法, RTBasic 里不存在
POLLUTION = {
    'SQRT': 'RTBasic 是 SQR(x), 不是 SQRT(x)',
    'STR': 'RTBasic 数值转字符串是 TOSTR(x), 不是 STR(x)',
    'WAITS': 'RTBasic 无 WAITS, 等待停止用 WAIT IDLE',
    'IN_POS': 'RTBasic 到位状态是 AXISEMG_IN, 不是 IN_POS',
    'MC_MOVEABS': 'RTBasic 无 PLCopen MC_* 函数块',
    'TC_MOVEABS': 'RTBasic 无 IEC TC_* 函数块',
    'TRIOBASIC': '方言已改为 RTBasic',
    'PLCOPEN': 'RTSys 未启用 PLCopen 库',
    'ALARM_A': 'RTBasic 无 IEC 功能块',
    'TC_MOVECIRC': 'RTBasic 无 IEC TC_* 函数块',
    'TC_CONNECT': 'RTBasic 无 IEC TC_* 函数块',
    'TC_CAM': 'RTBasic 无 IEC TC_* 函数块',
    'FUNCTION': 'RTBasic 无 Function 关键字, 用 SUB ... END SUB',
    'END_FUNCTION': 'RTBasic 无 End Function',
    'END_VAR': 'RTBasic 无 VAR...END_VAR',
    'ENDIF ': 'RTBasic 是 ENDIF 连写 (检查是否写成 END IF)',
    'REM': 'RTBasic 注释用 \' , 没有 REM',
    'DP': 'RTBasic 轴指令位置是 DPOS, 不是 DP',
    'MAX': 'RTBasic 无 MAX/MIN 函数, 参考库里没有',
    'MIN': 'RTBasic 无 MAX/MIN 函数, 参考库里没有',
    'RAND': 'RTBasic 无 RAND, 参考库里没有',
    'BIT': 'RTBasic 位运算用 READ_BIT/SET_BIT/CLEAR_BIT',
    'LEN': 'RTBasic 字符串长度是 STRLEN, 不是 LEN',
    'INSTR': 'RTBasic 字符串查找是 STRFIND, 不是 INSTR',
    'MID': 'RTBasic 无 MID, 用 LEFT/RIGHT/STRFIND 等',
    'LEFT': 'RTBasic 参考库里没有 LEFT 函数',
    'RIGHT': 'RTBasic 参考库里没有 RIGHT 函数',
    'UNVERIFIED': '非语言关键字, 已在前缀处理, 不算命令',
    'ALL': 'LOCK ALL 是危险锁定指令(已列安全规则)',
    'PROGRAM': 'RTBasic 程序用子程序 SUB 定义, 无 PROGRAM 关键字',
    'AS': 'RTBasic DIM 无 As 子句, 错写为 DIM x AS FLOAT',
    'FLOAT': 'RTBasic DIM 无类型子句',
    'DO': 'RTBasic 用 WHILE...WEND / REPEAT...UNTIL, 无 DO...LOOP',
    'LOOP': 'RTBasic 用 WHILE...WEND / REPEAT...UNTIL, 无 DO...LOOP',
    'SERIAL': 'RTBasic 参考库里没有 SERIAL 系统变量',
    'OUT': 'RTBasic 数字输出是 OP, 不是 OUT',
    'RAPID': 'RTBasic 急停是 RAPIDSTOP, 不是 RAPID',
    'HOME': 'RTBasic 回零是 DATUM, 不是 HOME',
    'INTEGER': 'RTBasic DIM 无类型子句',
    'STRING': 'RTBasic DIM 无类型子句',
    'MYMOVE': '用户变量示例, 不是内置命令',
    'MFAST': 'RTBasic 参考库里没有 MFAST',
    'MSYNC': 'RTBasic 参考库里没有 MSYNC',
    'GEAR': 'RTBasic 电子齿轮是 CONNECT, 不是 GEAR',
    'COMPARE': 'RTBasic 无 COMPARE 关键字',
}

# 代码里允许出现但不属于命令的标识符 (C# 语法糖 / 工具名 / 文档占位)
IGNORE = {
    'READ_SOURCE', 'WRITE_SOURCE', 'PATCH_SOURCE', 'COMPILE_PROGRAM', 'RUN_PROGRAM',
    'STOP_PROGRAM', 'LIST_PROGRAMS', 'GET_STATUS', 'LOOKUP_COMMAND', 'READ_SKILL',
    'DISCOVER_SKILLS', 'SEARCH_CODE', 'CREATE_PROGRAM', 'READ_VR', 'READ_TABLE',
    'READ_SYSVAR', 'LIST_AXES', 'GET_AXIS_DETAIL', 'LIST_PROCESSES', 'GET_PROCESS_VARIABLE',
    'GET_EVENTS', 'READ_DRIVE_PARAM', 'SCAN_ETHERCAT', 'READ_ETHERCAT_SDO',
    'GET_IEC_TASK_DETAIL', 'READ_IEC_VARIABLES', 'WRITE_IEC_VARIABLES', 'TASK_CREATE',
    'TASK_UPDATE', 'EXIT_PLAN_MODE', 'UPDATE_MEMORY', 'LIST_BREAKPOINTS', 'SET_BREAKPOINT',
    'CLEAR_ALL_BREAKPOINTS', 'LIST_PROJECT_ITEMS', 'LIST_DESCRIPTORS', 'POU_NAME',
    'MAIN', 'IF', 'ELSE', 'ELSEIF', 'ENDIF', 'FOR', 'NEXT', 'WHILE', 'WEND', 'REPEAT',
    'UNTIL', 'GOTO', 'GOSUB', 'RETURN', 'GLOBAL', 'LOCAL', 'DIM', 'CONST', 'SUB',
    'END', 'THEN', 'AND', 'OR', 'NOT', 'MOD', 'ON', 'OFF', 'TRUE', 'FALSE', 'NULL',
    'ABS_POS', 'AXIS_DONE', 'CYCLE_INDEX', 'STEP_COUNT', 'TORQUE', 'CURRENT',
    'TEMPERATURE', 'TEMP', 'VR_COUNT', 'FOR_X', 'MY', 'USR', 'G', 'X', 'Y', 'Z',
    'I', 'N', 'Q', 'T', 'P', 'K', 'M', 'J', 'A', 'B', 'C', 'D', 'E', 'F', 'H', 'L',
    'O', 'R', 'S', 'U', 'V', 'W',
}

# 反引号代码块
RE_BACKTICK = re.compile(r'`([^`\n]{1,300})`')
# 标识符: 大写字母/数字/下划线, 至少 2 字符
RE_IDENT = re.compile(r'\b([A-Z][A-Z0-9_]{1,})\b')
# 函数调用形态 NAME(
RE_CALL = re.compile(r'\b([A-Z][A-Z0-9_]{1,})\s*\(')
# 行首赋值左侧 (RTBasic 风格:  NAME = ...)
RE_ASSIGN = re.compile(r'(?m)^\s*([A-Z][A-Z0-9_]{1,})\s*(?:=[^=]|\+=|-=)')
# 关键字小写形式 (rtbasic 官方语法里 ENDIF/THEN 等是连写的)
RE_WORD = re.compile(r'\b([A-Za-z][A-Za-z0-9_]{1,})\b')


def load_commands():
    if not INDEX.exists():
        sys.exit(f'[错误] 找不到参考索引 {INDEX}\n先运行 tools/build_rtbasic_skills.py')
    idx = json.load(open(INDEX, encoding='utf-8'))
    return {e['name'].upper() for e in idx}


def code_contexts(text):
    """取出所有"代码上下文"片段: 反引号块 + 行首赋值行。"""
    out = []
    out.extend(m.group(1) for m in RE_BACKTICK.finditer(text))
    out.extend(line for line in text.splitlines() if RE_ASSIGN.match(line))
    return out


def scan_file(path, commands, verbose=False):
    text = path.read_text(encoding='utf-8', errors='replace')
    problems = []   # (kind, token, detail, context)
    seen = set()

    # 1) 代码上下文里的标识符必须真实存在
    for ctx in code_contexts(text):
        for tok in set(RE_IDENT.findall(ctx)) | set(RE_CALL.findall(ctx)):
            if tok in commands or tok in IGNORE:
                continue
            key = ('unknown', tok)
            if key in seen:
                continue
            seen.add(key)
            problems.append(('未知标识符', tok, '参考库中不存在', ctx.strip()[:70]))

    # 2) 方言污染黑名单 (全文扫描)
    for tok, why in POLLUTION.items():
        t = tok.strip()
        if not t or t in commands:
            continue
        for m in re.finditer(r'(?<![A-Za-z0-9_])' + re.escape(t) + r'(?![A-Za-z0-9_])', text):
            line_no = text.count('\n', 0, m.start()) + 1
            line = text.splitlines()[line_no - 1].strip()[:80]
            key = ('pollution', tok)
            if key in seen:
                break
            seen.add(key)
            problems.append(('方言污染', tok, why, f'L{line_no}: {line}'))

    rel = path.relative_to(REPO)
    if problems:
        print(f'\n[问题] {rel}  ({len(problems)} 项)')
        for kind, tok, why, ctx in problems:
            print(f'   {kind:10s} {tok:18s} {why}')
            print(f'               └─ {ctx}')
    else:
        print(f'[OK]   {rel}')
    return problems


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--verbose', action='store_true')
    args = ap.parse_args()

    commands = load_commands()
    print(f'参考库真实命令数: {len(commands)}')

    total = 0
    for path in TARGETS:
        if not path.exists():
            print(f'[跳过] {path.name} 不存在')
            continue
        total += len(scan_file(path, commands, args.verbose))

    print(f'\n合计问题: {total}')
    return 1 if total else 0


if __name__ == '__main__':
    sys.exit(main())
