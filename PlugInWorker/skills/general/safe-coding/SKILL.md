---
name: safe-coding
description: Safe motion programming patterns for Trio controllers — avoid lockups, ensure safe stops, handle axis errors.
---

# Safe Motion Coding Guidelines

## Mandatory Safety Rules

- Never write infinite loops without an exit condition that checks axis state.
- Always check `MOTION_ERROR` and `AXIS_ERROR` bits inside control loops.
- Use `WDOG = OFF` as a fallback if a fatal error is detected.
- Never disable a drive or release a brake while an axis is moving.

## Standard Pattern: Move with Timeout

```trio
base(axis)
speed = 100
accel = 1000
decel = 1000
 creeping = 10
move(abs_pos) ' target position
t = TIME
while DPOS <> abs_pos
    if TIME - t > 5000 then
        ' Timeout: abort and report
        cancel
        print "Move timeout on axis "; axis
        WDOG = OFF
        return
    endif
    if (IN(motion_error_input)) then
        cancel
        WDOG = OFF
        print "Motion error"
        return
    endif
wend
```

## Pre-Run Checklist

1. Are all axes homed (`DATUM` complete)?
2. Is the e-stop input monitored?
3. Does the program have a safe stop path on error?
4. Are soft limits configured (`FE_LIMIT`)?
5. Is the cycle-stop input wired into the main loop?

## Forbidden Commands (Never Emit)

- `LOCK`, `LOCK AXIS`, `LOCK ALL` — these brick the controller
- Any code that masks `MOTION_ERROR` permanently
- `SERVO = OFF` inside a position loop without checking `MSPEED == 0`
