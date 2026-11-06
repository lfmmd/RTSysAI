"""注册 RTSys 插件到注册表，并部署 DLL 到 Plug_in 目录。

RTSys.exe 通过 MFC CWinApp profile 读取：
  HKCU\Software\RTSys Application 1.0\RTSys\Plugin
    Count       REG_DWORD  插件数
    PluginN     REG_SZ     DLL 文件名
    Menu nameN  REG_SZ     菜单显示名
    Menu promptN REG_SZ    提示
    DirN        REG_SZ     Plug_in 下的子目录名
"""
import os
import shutil
import sys
import winreg

RTSYS_DIR = r"%USERPROFILE%\Desktop\RTSys"
KEY_PATH = r"Software\RTSys Application 1.0\RTSys\Plugin"

MODULE_NAME = "AIPlugin"
DLL_NAME = "RTSysPlugInNative.dll"
MENU_NAME = "AI 插件"
MENU_PROMPT = "AI 助手插件"

PROJECT_DEPLOY = r"%USERPROFILE%\Desktop\works\motion perfect\TrioAI_RTSys\deploy\PlugInNative"
RUNTIME_DEPS = [
    "RTSysPlugInNative.dll",
    "RTSysPlugInAdapter.dll",
    "TrioSharedLibrary.dll",
]


def deploy() -> str:
    """把插件与依赖复制到 RTSys\\Plug_in\\<MODULE_NAME>\\"""
    dest_dir = os.path.join(RTSYS_DIR, "Plug_in", MODULE_NAME)
    os.makedirs(dest_dir, exist_ok=True)

    copied = []
    for name in RUNTIME_DEPS:
        src = os.path.join(PROJECT_DEPLOY, name)
        if not os.path.exists(src):
            print(f"  [跳过] 源文件不存在: {src}")
            continue
        shutil.copy2(src, os.path.join(dest_dir, name))
        copied.append(name)

    print(f"部署到 {dest_dir}")
    for name in copied:
        size = os.path.getsize(os.path.join(dest_dir, name))
        print(f"  + {name} ({size} bytes)")
    return dest_dir


def register(index: int = 0) -> None:
    """写入注册表登记插件。"""
    key = winreg.CreateKeyEx(
        winreg.HKEY_CURRENT_USER, KEY_PATH, 0, winreg.KEY_ALL_ACCESS
    )
    try:
        winreg.SetValueEx(key, "Count", 0, winreg.REG_DWORD, 1)
        winreg.SetValueEx(key, f"Plugin{index}", 0, winreg.REG_SZ, DLL_NAME)
        winreg.SetValueEx(key, f"Menu name{index}", 0, winreg.REG_SZ, MENU_NAME)
        winreg.SetValueEx(key, f"Menu prompt{index}", 0, winreg.REG_SZ, MENU_PROMPT)
        winreg.SetValueEx(key, f"Dir{index}", 0, winreg.REG_SZ, MODULE_NAME)
    finally:
        winreg.CloseKey(key)
    print(f"注册表已写入: HKCU\\{KEY_PATH}")


def verify() -> None:
    """读回注册表确认写入正确。"""
    key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, KEY_PATH)
    try:
        print("=== 注册表当前内容 ===")
        value_count = winreg.QueryInfoKey(key)[1]
        for i in range(value_count):
            name, value, _ = winreg.EnumValue(key, i)
            print(f"  {name} = {value!r}")
    finally:
        winreg.CloseKey(key)


if __name__ == "__main__":
    if "--no-deploy" not in sys.argv:
        deploy()
    register()
    verify()
    print("\n完成。重启 RTSys 后宿主会加载该插件。")
