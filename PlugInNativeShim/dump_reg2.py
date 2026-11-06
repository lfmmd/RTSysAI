import winreg

def dump(path):
    print("### KEY:", path)
    try:
        k = winreg.OpenKey(winreg.HKEY_CURRENT_USER, path)
    except OSError as e:
        print("   OPEN ERR", e)
        return
    i = 0
    while True:
        try:
            name, val, typ = winreg.EnumValue(k, i)
        except OSError:
            break
        print("   VAL", repr(name), "=", repr(val), "type", typ)
        i += 1
    j = 0
    subs = []
    while True:
        try:
            sub = winreg.EnumKey(k, j)
        except OSError:
            break
        subs.append(sub)
        j += 1
    print("   SUBKEYS:", subs)
    k.Close()

base = r"Software\RTSys Application 1.0"
dump(base)
dump(base + r"\RTSys")
dump(base + r"\RTSys\Plugin")
