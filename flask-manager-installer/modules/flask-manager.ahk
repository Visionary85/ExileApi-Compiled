; =============================================================================
; Flask Manager  –  Exile-UI module
; Auto-presses flask keys when life/mana drops below a calibrated pixel threshold.
;
; ACCESS after install:
;   • Right-click the AHK tray icon → "Flask Manager Settings..."
;   • Or use the configured settings hotkey (default: Ctrl+Shift+Alt+F)
;
; DETECTION METHOD:
;   A reference pixel is sampled on the orb at the user's chosen height while
;   the orb is FULL.  If that pixel deviates beyond the tolerance value
;   (Euclidean RGB distance), the orb has drained past the threshold → flask fires.
;
; OPTIONAL: integrate into the main Exile-UI settings menu manually:
;   1. In modules\settings menu.ahk, find the tab/list string and append:
;      |Flask Manager
;   2. In the per-tab dispatch, add:
;      Case "Flask Manager":  Settings_flaskmanager()
; =============================================================================

Init_flaskmanager()
{
    local
    global vars, settings

    settings.flaskmanager := {}
    vars.flaskmanager     := {}

    ini := "ini" vars.poe_version "\flask-manager.ini"

    settings.flaskmanager.enabled      := LLK_IniRead(ini, "Settings", "enabled",      0)
    settings.flaskmanager.use_life     := LLK_IniRead(ini, "Settings", "use_life",     0)
    settings.flaskmanager.use_mana     := LLK_IniRead(ini, "Settings", "use_mana",     0)
    settings.flaskmanager.life_key     := LLK_IniRead(ini, "Settings", "life_key",     "1")
    settings.flaskmanager.mana_key     := LLK_IniRead(ini, "Settings", "mana_key",     "2")
    settings.flaskmanager.life_cd      := LLK_IniRead(ini, "Settings", "life_cd",      500)
    settings.flaskmanager.mana_cd      := LLK_IniRead(ini, "Settings", "mana_cd",      500)
    settings.flaskmanager.tolerance    := LLK_IniRead(ini, "Settings", "tolerance",    35)
    settings.flaskmanager.settings_key := LLK_IniRead(ini, "Settings", "settings_key", "^+!f")

    vars.flaskmanager.life_last  := 0
    vars.flaskmanager.mana_last  := 0

    vars.flaskmanager.life_x     := LLK_IniRead(ini, "Calibration", "life_x",     "")
    vars.flaskmanager.life_y     := LLK_IniRead(ini, "Calibration", "life_y",     "")
    vars.flaskmanager.life_color := LLK_IniRead(ini, "Calibration", "life_color", "")
    vars.flaskmanager.mana_x     := LLK_IniRead(ini, "Calibration", "mana_x",     "")
    vars.flaskmanager.mana_y     := LLK_IniRead(ini, "Calibration", "mana_y",     "")
    vars.flaskmanager.mana_color := LLK_IniRead(ini, "Calibration", "mana_color", "")

    ; Tray menu entry (right-click tray icon to access settings)
    Menu, Tray, Add
    Menu, Tray, Add, Flask Manager Settings..., Flaskmanager_OpenSettings

    ; Optional quick-access hotkey
    If !Blank(settings.flaskmanager.settings_key)
        Hotkey, % settings.flaskmanager.settings_key, Flaskmanager_OpenSettings, On

    If settings.flaskmanager.enabled
        SetTimer, Flaskmanager_Loop, 100
}

; ------------------------------------------------------------------
; Detection timer – fires every 100 ms while PoE is in focus.
; ------------------------------------------------------------------
Flaskmanager_Loop()
{
    local
    global vars, settings

    If !WinActive("ahk_group poe_clients")
        Return

    fm  := vars.flaskmanager
    now := A_TickCount
    tol := settings.flaskmanager.tolerance

    If settings.flaskmanager.use_life && !Blank(fm.life_x) && !Blank(fm.life_color)
    {
        PixelGetColor, cur_life, % fm.life_x, % fm.life_y, RGB
        If (Flaskmanager_ColorDist(cur_life, fm.life_color) > tol) && (now - fm.life_last > settings.flaskmanager.life_cd)
        {
            SendInput, % settings.flaskmanager.life_key
            vars.flaskmanager.life_last := A_TickCount
        }
    }

    If settings.flaskmanager.use_mana && !Blank(fm.mana_x) && !Blank(fm.mana_color)
    {
        PixelGetColor, cur_mana, % fm.mana_x, % fm.mana_y, RGB
        If (Flaskmanager_ColorDist(cur_mana, fm.mana_color) > tol) && (now - fm.mana_last > settings.flaskmanager.mana_cd)
        {
            SendInput, % settings.flaskmanager.mana_key
            vars.flaskmanager.mana_last := A_TickCount
        }
    }
}

Flaskmanager_ColorDist(c1, c2)
{
    local
    r1 := (c1 >> 16) & 0xFF,  g1 := (c1 >> 8) & 0xFF,  b1 := c1 & 0xFF
    r2 := (c2 >> 16) & 0xFF,  g2 := (c2 >> 8) & 0xFF,  b2 := c2 & 0xFF
    Return Sqrt((r1-r2)**2 + (g1-g2)**2 + (b1-b2)**2)
}

Flaskmanager_Toggle(enable)
{
    local
    global vars, settings

    settings.flaskmanager.enabled := enable
    IniWrite, %enable%, % "ini" vars.poe_version "\flask-manager.ini", Settings, enabled

    If enable
        SetTimer, Flaskmanager_Loop, 100
    Else
        SetTimer, Flaskmanager_Loop, Off
}

; ==================================================================
; STANDALONE SETTINGS WINDOW
; Accessible via tray menu or hotkey – no settings menu.ahk edit needed.
; ==================================================================
Flaskmanager_OpenSettings()
{
    local
    global vars, settings

    ; Only one instance
    If WinExist("ahk_id " vars.hwnd.fm_settings)
    {
        WinActivate, ahk_id % vars.hwnd.fm_settings
        Return
    }

    ini := "ini" vars.poe_version "\flask-manager.ini"
    fn  := settings.general.font
    fs  := settings.general.fSize
    fw  := settings.general.fWidth
    sp  := vars.settings.spacing

    Gui, fm_settings: New, -Caption +AlwaysOnTop +ToolWindow +LastFound HWNDfms_hwnd
    Gui, fm_settings: Color, 111111
    Gui, fm_settings: Margin, 14, 12
    vars.hwnd.fm_settings := fms_hwnd

    ; ── title bar ──────────────────────────────────────────────────
    Gui, fm_settings: Font, % "s" fs+2 " cWhite Bold", %fn%
    Gui, fm_settings: Add, Text,   xs,                  Flask Manager
    Gui, fm_settings: Font, % "s" fs   " c888888 Norm", %fn%
    Gui, fm_settings: Add, Text,   ys,                  auto-flask for life && mana
    Gui, fm_settings: Add, Button, % "ys gFMS_Close w" fw*3, X

    ; ── master enable ──────────────────────────────────────────────
    Gui, fm_settings: Font, % "s" fs " cWhite Norm", %fn%
    Gui, fm_settings: Add, Checkbox
        , % "xs y+10 Section gFMS_Change HWNDhwnd Checked" settings.flaskmanager.enabled
        , %  " enabled  (master switch)"
    vars.hwnd.fms_enabled := hwnd

    ; ── life flask ─────────────────────────────────────────────────
    Gui, fm_settings: Font, % "s" fs " c4488FF Bold", %fn%
    Gui, fm_settings: Add, Text, % "xs y+" sp*2,   LIFE FLASK
    Gui, fm_settings: Font, % "s" fs " cWhite Norm", %fn%

    Gui, fm_settings: Add, Checkbox
        , % "ys gFMS_Change HWNDhwnd Checked" settings.flaskmanager.use_life
        , %  " use life flask"
    vars.hwnd.fms_use_life := hwnd

    Gui, fm_settings: Add, Text,  % "xs y+" sp " Section",          % "  flask key:"
    Gui, fm_settings: Add, Edit
        , % "ys hp cBlack gFMS_Change HWNDhwnd w" fw*5
        , % settings.flaskmanager.life_key
    vars.hwnd.fms_life_key := hwnd
    Gui, fm_settings: Font, % "s" fs-1 " c888888", %fn%
    Gui, fm_settings: Add, Text, ys hp, % "  (AHK key: 1  2  q  {F1}  etc.)"
    Gui, fm_settings: Font, % "s" fs " cWhite", %fn%

    Gui, fm_settings: Add, Text,  % "xs y+" sp " Section",          % "  cooldown (ms):"
    Gui, fm_settings: Add, Edit
        , % "ys hp cBlack gFMS_Change HWNDhwnd w" fw*6 " Number"
        , % settings.flaskmanager.life_cd
    vars.hwnd.fms_life_cd := hwnd

    Gui, fm_settings: Add, Button
        , % "xs y+" sp " gFMS_CalLife w" fw*22
        , %  " Calibrate Life Orb threshold..."
    If !Blank(vars.flaskmanager.life_color)
    {
        Gui, fm_settings: Font, % "s" fs-1 " c00CC66", %fn%
        Gui, fm_settings: Add, Text, % "xs y+3"
            , % "  calibrated  –  pixel: (" vars.flaskmanager.life_x ", " vars.flaskmanager.life_y ")"
              . "   ref: " vars.flaskmanager.life_color
    }
    Else
    {
        Gui, fm_settings: Font, % "s" fs-1 " cFFAA00", %fn%
        Gui, fm_settings: Add, Text, % "xs y+3", % "  not calibrated – click button above"
    }
    Gui, fm_settings: Font, % "s" fs " cWhite Norm", %fn%

    ; ── mana flask ─────────────────────────────────────────────────
    Gui, fm_settings: Font, % "s" fs " c8844FF Bold", %fn%
    Gui, fm_settings: Add, Text, % "xs y+" sp*2,   MANA FLASK
    Gui, fm_settings: Font, % "s" fs " cWhite Norm", %fn%

    Gui, fm_settings: Add, Checkbox
        , % "ys gFMS_Change HWNDhwnd Checked" settings.flaskmanager.use_mana
        , %  " use mana flask"
    vars.hwnd.fms_use_mana := hwnd

    Gui, fm_settings: Add, Text,  % "xs y+" sp " Section",          % "  flask key:"
    Gui, fm_settings: Add, Edit
        , % "ys hp cBlack gFMS_Change HWNDhwnd w" fw*5
        , % settings.flaskmanager.mana_key
    vars.hwnd.fms_mana_key := hwnd
    Gui, fm_settings: Font, % "s" fs-1 " c888888", %fn%
    Gui, fm_settings: Add, Text, ys hp, % "  (AHK key: 1  2  q  {F1}  etc.)"
    Gui, fm_settings: Font, % "s" fs " cWhite", %fn%

    Gui, fm_settings: Add, Text,  % "xs y+" sp " Section",          % "  cooldown (ms):"
    Gui, fm_settings: Add, Edit
        , % "ys hp cBlack gFMS_Change HWNDhwnd w" fw*6 " Number"
        , % settings.flaskmanager.mana_cd
    vars.hwnd.fms_mana_cd := hwnd

    Gui, fm_settings: Add, Button
        , % "xs y+" sp " gFMS_CalMana w" fw*22
        , %  " Calibrate Mana Orb threshold..."
    If !Blank(vars.flaskmanager.mana_color)
    {
        Gui, fm_settings: Font, % "s" fs-1 " c00CC66", %fn%
        Gui, fm_settings: Add, Text, % "xs y+3"
            , % "  calibrated  –  pixel: (" vars.flaskmanager.mana_x ", " vars.flaskmanager.mana_y ")"
              . "   ref: " vars.flaskmanager.mana_color
    }
    Else
    {
        Gui, fm_settings: Font, % "s" fs-1 " cFFAA00", %fn%
        Gui, fm_settings: Add, Text, % "xs y+3", % "  not calibrated – click button above"
    }
    Gui, fm_settings: Font, % "s" fs " cWhite Norm", %fn%

    ; ── tolerance ──────────────────────────────────────────────────
    Gui, fm_settings: Font, % "s" fs " c888888", %fn%
    Gui, fm_settings: Add, Text, % "xs y+" sp*2 " Section"
        , % "color tolerance  (10–100,  lower = more sensitive):"
    Gui, fm_settings: Font, % "s" fs " cWhite", %fn%
    Gui, fm_settings: Add, Slider
        , % "xs y+4 gFMS_Change HWNDhwnd w" fw*24 " Range10-100 TickInterval10 NoTicks"
        , % settings.flaskmanager.tolerance
    vars.hwnd.fms_tolerance := hwnd
    Gui, fm_settings: Add, Text, % "ys hp HWNDhwnd w" fw*5, % "  " settings.flaskmanager.tolerance
    vars.hwnd.fms_tol_label := hwnd

    ; ── settings hotkey ────────────────────────────────────────────
    Gui, fm_settings: Font, % "s" fs " c888888", %fn%
    Gui, fm_settings: Add, Text, % "xs y+" sp*2 " Section", % "settings hotkey:"
    Gui, fm_settings: Font, % "s" fs " cWhite", %fn%
    Gui, fm_settings: Add, Edit
        , % "ys hp cBlack gFMS_Change HWNDhwnd w" fw*8
        , % settings.flaskmanager.settings_key
    vars.hwnd.fms_settings_key := hwnd
    Gui, fm_settings: Font, % "s" fs-1 " c888888", %fn%
    Gui, fm_settings: Add, Text, ys hp, % "  (takes effect after restart)"
    Gui, fm_settings: Font, % "s" fs " cWhite Norm", %fn%

    Gui, fm_settings: Show, % "NA x" (vars.client.x + vars.client.w//2 - fw*20)
                                 . " y" (vars.client.y + vars.client.h//4)
}

FMS_Close:
{
    global vars
    Gui, fm_settings: Destroy
    vars.hwnd.fm_settings := ""
    Return
}

FMS_Change:
{
    local
    global vars, settings

    ini  := "ini" vars.poe_version "\flask-manager.ini"
    ctrl := A_GuiControl

    If (ctrl = vars.hwnd.fms_enabled)
    {
        GuiControlGet, val, , %ctrl%
        Flaskmanager_Toggle(val)
    }
    Else If (ctrl = vars.hwnd.fms_use_life)
    {
        GuiControlGet, val, , %ctrl%
        settings.flaskmanager.use_life := val
        IniWrite, %val%, %ini%, Settings, use_life
    }
    Else If (ctrl = vars.hwnd.fms_use_mana)
    {
        GuiControlGet, val, , %ctrl%
        settings.flaskmanager.use_mana := val
        IniWrite, %val%, %ini%, Settings, use_mana
    }
    Else If (ctrl = vars.hwnd.fms_life_key)
    {
        GuiControlGet, val, , %ctrl%
        settings.flaskmanager.life_key := val
        IniWrite, %val%, %ini%, Settings, life_key
    }
    Else If (ctrl = vars.hwnd.fms_mana_key)
    {
        GuiControlGet, val, , %ctrl%
        settings.flaskmanager.mana_key := val
        IniWrite, %val%, %ini%, Settings, mana_key
    }
    Else If (ctrl = vars.hwnd.fms_life_cd)
    {
        GuiControlGet, val, , %ctrl%
        settings.flaskmanager.life_cd := val
        IniWrite, %val%, %ini%, Settings, life_cd
    }
    Else If (ctrl = vars.hwnd.fms_mana_cd)
    {
        GuiControlGet, val, , %ctrl%
        settings.flaskmanager.mana_cd := val
        IniWrite, %val%, %ini%, Settings, mana_cd
    }
    Else If (ctrl = vars.hwnd.fms_tolerance)
    {
        GuiControlGet, val, , %ctrl%
        settings.flaskmanager.tolerance := val
        IniWrite, %val%, %ini%, Settings, tolerance
        GuiControl, , % vars.hwnd.fms_tol_label, % "  " val
    }
    Else If (ctrl = vars.hwnd.fms_settings_key)
    {
        GuiControlGet, val, , %ctrl%
        settings.flaskmanager.settings_key := val
        IniWrite, %val%, %ini%, Settings, settings_key
    }
    Return
}

FMS_CalLife:
{
    Gui, fm_settings: Destroy
    global vars
    vars.hwnd.fm_settings := ""
    Flaskmanager_Calibrate("life")
    Return
}

FMS_CalMana:
{
    Gui, fm_settings: Destroy
    global vars
    vars.hwnd.fm_settings := ""
    Flaskmanager_Calibrate("mana")
    Return
}

; ==================================================================
; CALIBRATION WIZARD
; ==================================================================
Flaskmanager_Calibrate(type)
{
    local
    global vars, settings

    vars.flaskmanager.cal_type := type

    label := (type = "life") ? "Life Orb"  : "Mana Orb"
    hint  := (type = "life") ? "bottom-left" : "bottom-right"
    fn    := settings.general.font
    fs    := settings.general.fSize
    fw    := settings.general.fWidth

    Gui, fm_cal: New, -Caption +AlwaysOnTop +ToolWindow +LastFound HWNDfm_cal_hwnd
    Gui, fm_cal: Color, 111111
    Gui, fm_cal: Margin, 14, 12
    Gui, fm_cal: Font, % "s" fs+1 " cWhite Bold", %fn%
    Gui, fm_cal: Add, Text, xs, % "Calibrate: " label
    Gui, fm_cal: Font, % "s" fs " cSilver Norm", %fn%
    Gui, fm_cal: Add, Text, % "xs y+8 w" fw*40
        , % "HOW IT WORKS`nA single pixel at your chosen position is sampled while your " label " is FULL.`nDuring play, if that pixel changes color (orb drained below that point),`na flask key is sent automatically."
    Gui, fm_cal: Font, % "s" fs " cWhite", %fn%
    Gui, fm_cal: Add, Text, % "xs y+8", Steps:
    Gui, fm_cal: Font, % "s" fs " cSilver", %fn%
    Gui, fm_cal: Add, Text, % "xs y+4 w" fw*40
        , % "1.  Fill your " label " completely`n2.  Click OK`n3.  Hold Left Mouse Button over your " label " (" hint ")`n      at the HEIGHT representing your trigger threshold`n      e.g. hold at 60%% height of the orb → fires when life < 60%%`n4.  Release LMB to capture and save"
    Gui, fm_cal: Font, % "s" fs " cWhite Norm", %fn%
    Gui, fm_cal: Add, Button, % "xs y+12 w" fw*8  " gFM_Cal_OK",     OK
    Gui, fm_cal: Add, Button, % "x+6      w" fw*8  " gFM_Cal_Cancel", Cancel
    Gui, fm_cal: Show, % "NA x" (vars.client.x + vars.client.w//2 - fw*20)
                             . " y" (vars.client.y + vars.client.h//4)
}

FM_Cal_OK:
{
    local
    global vars
    cal_type := vars.flaskmanager.cal_type
    Gui, fm_cal: Destroy
    Flaskmanager_Calibrate2(cal_type)
    Return
}

FM_Cal_Cancel:
{
    Gui, fm_cal: Destroy
    Return
}

Flaskmanager_Calibrate2(type)
{
    local
    global vars, settings

    KeyWait, LButton, U
    KeyWait, LButton, D

    Gui, fm_zoom:  New, -Caption +E0x80000 +E0x20 +LastFound +AlwaysOnTop +ToolWindow HWNDfm_zoom_hwnd
    Gui, fm_zoom:  Show, NA
    Gui, fm_cross: New, -Caption -DPIScale +LastFound +AlwaysOnTop +ToolWindow
                      +E0x20 +E0x02000000 +E0x00080000 HWNDfm_cross_hwnd
    Gui, fm_cross: Color, Aqua
    Gui, fm_cross: Margin, 0, 0
    Gui, fm_cross: Add, Text, BackgroundTrans w1 h1

    While GetKeyState("LButton", "P")
    {
        MouseGetPos, mx, my
        pBitmap := Gdip_BitmapFromScreen(mx - 5 "|" my - 5 "|" 11 "|" 11)
        hbm     := CreateDIBSection(88, 88)
        hdc     := CreateCompatibleDC()
        obm     := SelectObject(hdc, hbm)
        gfx     := Gdip_GraphicsFromHDC(hdc)
        Gdip_SetInterpolationMode(gfx, 5)
        Gdip_DrawImage(gfx, pBitmap, 0, 0, 88, 88, 0, 0, 11, 11)
        UpdateLayeredWindow(fm_zoom_hwnd, hdc, mx - 100, my - 44, 88, 88)
        Gdip_DisposeImage(pBitmap)
        SelectObject(hdc, obm)
        DeleteObject(hbm)
        DeleteDC(hdc)
        Gdip_DeleteGraphics(gfx)
        Gui, fm_cross: Show, % "NA x" mx " y" my - 5
        Sleep, 50
    }
    Gui, fm_cross: Destroy
    Gui, fm_zoom:  Destroy

    MouseGetPos, cx, cy
    PixelGetColor, sampled, %cx%, %cy%, RGB

    If Blank(sampled) || (sampled = 0)
    {
        MsgBox, 48, Flask Manager, Could not sample color at that position.`nPlease try again., 5
        Return
    }

    ini := "ini" vars.poe_version "\flask-manager.ini"
    IniWrite, %cx%,      %ini%, Calibration, % type "_x"
    IniWrite, %cy%,      %ini%, Calibration, % type "_y"
    IniWrite, %sampled%, %ini%, Calibration, % type "_color"

    vars.flaskmanager[type "_x"]     := cx
    vars.flaskmanager[type "_y"]     := cy
    vars.flaskmanager[type "_color"] := sampled

    Flaskmanager_CalibrationDone((type = "life") ? "Life Orb" : "Mana Orb", cx, cy, sampled)
}

Flaskmanager_CalibrationDone(label, cx, cy, color)
{
    local
    global vars, settings

    fn := settings.general.font
    fs := settings.general.fSize
    fw := settings.general.fWidth

    Gui, fm_done: New, -Caption +AlwaysOnTop +ToolWindow +LastFound HWNDfm_done_hwnd
    Gui, fm_done: Color, 111111
    Gui, fm_done: Margin, 14, 12
    Gui, fm_done: Font, % "s" fs+1 " c00FF88 Bold", %fn%
    Gui, fm_done: Add, Text, xs, % label " calibrated"
    Gui, fm_done: Font, % "s" fs " cSilver Norm", %fn%
    Gui, fm_done: Add, Text, % "xs y+6 w" fw*32
        , % "Pixel position :  (" cx ",  " cy ")`nReference color  :  " color "`n`nDetection is now active for this orb."
    Gui, fm_done: Font, % "s" fs " cWhite Norm", %fn%
    Gui, fm_done: Add, Button, % "xs y+10 w" fw*10 " gFM_Done_Close", Open Settings
    Gui, fm_done: Add, Button, % "x+6      w" fw*8  " gFM_Done_CloseOnly", Close
    Gui, fm_done: Show, % "NA x" (vars.client.x + vars.client.w//2 - fw*16)
                              . " y" (vars.client.y + vars.client.h//4)
}

FM_Done_Close:
{
    Gui, fm_done: Destroy
    Flaskmanager_OpenSettings()
    Return
}

FM_Done_CloseOnly:
{
    Gui, fm_done: Destroy
    Return
}

; ==================================================================
; OPTIONAL: settings menu.ahk integration (see header for instructions)
; ==================================================================
Settings_flaskmanager()
{
    local
    global vars, settings, GUI

    sp  := vars.settings.spacing
    fn  := settings.general.font
    fs  := settings.general.fSize
    fw  := settings.general.fWidth

    Gui, %GUI%: Add, Checkbox
        , % "xs y+" sp " Section gSettings_flaskmanager2 HWNDhwnd Checked" settings.flaskmanager.enabled
        , %  " auto-flask  (life && mana)"
    vars.hwnd.settings["fm_enabled"] := hwnd

    Gui, %GUI%: Font, % "s" fs " c4488FF Bold", %fn%
    Gui, %GUI%: Add, Text, % "xs y+" sp*2, LIFE FLASK
    Gui, %GUI%: Font, % "s" fs " cWhite Norm", %fn%
    Gui, %GUI%: Add, Checkbox
        , % "ys gSettings_flaskmanager2 HWNDhwnd Checked" settings.flaskmanager.use_life
        , %  " enabled"
    vars.hwnd.settings["fm_use_life"] := hwnd

    Gui, %GUI%: Add, Text,  % "xs y+" sp " Section", % "  key:"
    Gui, %GUI%: Add, Edit
        , % "ys hp cBlack gSettings_flaskmanager2 HWNDhwnd w" fw*5
        , % settings.flaskmanager.life_key
    vars.hwnd.settings["fm_life_key"] := hwnd

    Gui, %GUI%: Add, Text,  % "xs y+" sp " Section", % "  cooldown (ms):"
    Gui, %GUI%: Add, Edit
        , % "ys hp cBlack gSettings_flaskmanager2 HWNDhwnd w" fw*6 " Number"
        , % settings.flaskmanager.life_cd
    vars.hwnd.settings["fm_life_cd"] := hwnd

    Gui, %GUI%: Add, Button
        , % "xs y+" sp " gSettings_flaskmanager2 HWNDhwnd w" fw*22
        , %  " Calibrate Life Orb threshold..."
    vars.hwnd.settings["fm_cal_life"] := hwnd

    If !Blank(vars.flaskmanager.life_color)
    {
        Gui, %GUI%: Font, % "s" fs-1 " c00CC66"
        Gui, %GUI%: Add, Text, % "xs y+2"
            , % "  calibrated (" vars.flaskmanager.life_x ", " vars.flaskmanager.life_y ")"
    }
    Else
    {
        Gui, %GUI%: Font, % "s" fs-1 " cFFAA00"
        Gui, %GUI%: Add, Text, % "xs y+2", % "  not calibrated"
    }
    Gui, %GUI%: Font, % "s" fs " cWhite Norm"

    Gui, %GUI%: Font, % "s" fs " c8844FF Bold", %fn%
    Gui, %GUI%: Add, Text, % "xs y+" sp*2, MANA FLASK
    Gui, %GUI%: Font, % "s" fs " cWhite Norm", %fn%
    Gui, %GUI%: Add, Checkbox
        , % "ys gSettings_flaskmanager2 HWNDhwnd Checked" settings.flaskmanager.use_mana
        , %  " enabled"
    vars.hwnd.settings["fm_use_mana"] := hwnd

    Gui, %GUI%: Add, Text,  % "xs y+" sp " Section", % "  key:"
    Gui, %GUI%: Add, Edit
        , % "ys hp cBlack gSettings_flaskmanager2 HWNDhwnd w" fw*5
        , % settings.flaskmanager.mana_key
    vars.hwnd.settings["fm_mana_key"] := hwnd

    Gui, %GUI%: Add, Text,  % "xs y+" sp " Section", % "  cooldown (ms):"
    Gui, %GUI%: Add, Edit
        , % "ys hp cBlack gSettings_flaskmanager2 HWNDhwnd w" fw*6 " Number"
        , % settings.flaskmanager.mana_cd
    vars.hwnd.settings["fm_mana_cd"] := hwnd

    Gui, %GUI%: Add, Button
        , % "xs y+" sp " gSettings_flaskmanager2 HWNDhwnd w" fw*22
        , %  " Calibrate Mana Orb threshold..."
    vars.hwnd.settings["fm_cal_mana"] := hwnd

    If !Blank(vars.flaskmanager.mana_color)
    {
        Gui, %GUI%: Font, % "s" fs-1 " c00CC66"
        Gui, %GUI%: Add, Text, % "xs y+2"
            , % "  calibrated (" vars.flaskmanager.mana_x ", " vars.flaskmanager.mana_y ")"
    }
    Else
    {
        Gui, %GUI%: Font, % "s" fs-1 " cFFAA00"
        Gui, %GUI%: Add, Text, % "xs y+2", % "  not calibrated"
    }
    Gui, %GUI%: Font, % "s" fs " cWhite Norm"

    Gui, %GUI%: Font, % "s" fs " c888888"
    Gui, %GUI%: Add, Text, % "xs y+" sp*2 " Section", % "color tolerance  (10–100):"
    Gui, %GUI%: Font, % "s" fs " cWhite"
    Gui, %GUI%: Add, Slider
        , % "ys gSettings_flaskmanager2 HWNDhwnd w" fw*20 " Range10-100 TickInterval10 NoTicks"
        , % settings.flaskmanager.tolerance
    vars.hwnd.settings["fm_tolerance"] := hwnd
    Gui, %GUI%: Add, Text, % "ys hp HWNDhwnd w" fw*5, % "  " settings.flaskmanager.tolerance
    vars.hwnd.settings["fm_tol_label"] := hwnd
}

Settings_flaskmanager2()
{
    local
    global vars, settings

    ini  := "ini" vars.poe_version "\flask-manager.ini"
    ctrl := A_GuiControl

    If (ctrl = vars.hwnd.settings["fm_enabled"])
    {
        GuiControlGet, val, , %ctrl%
        Flaskmanager_Toggle(val)
    }
    Else If (ctrl = vars.hwnd.settings["fm_use_life"])
    {
        GuiControlGet, val, , %ctrl%
        settings.flaskmanager.use_life := val
        IniWrite, %val%, %ini%, Settings, use_life
    }
    Else If (ctrl = vars.hwnd.settings["fm_use_mana"])
    {
        GuiControlGet, val, , %ctrl%
        settings.flaskmanager.use_mana := val
        IniWrite, %val%, %ini%, Settings, use_mana
    }
    Else If (ctrl = vars.hwnd.settings["fm_life_key"])
    {
        GuiControlGet, val, , %ctrl%
        settings.flaskmanager.life_key := val
        IniWrite, %val%, %ini%, Settings, life_key
    }
    Else If (ctrl = vars.hwnd.settings["fm_mana_key"])
    {
        GuiControlGet, val, , %ctrl%
        settings.flaskmanager.mana_key := val
        IniWrite, %val%, %ini%, Settings, mana_key
    }
    Else If (ctrl = vars.hwnd.settings["fm_life_cd"])
    {
        GuiControlGet, val, , %ctrl%
        settings.flaskmanager.life_cd := val
        IniWrite, %val%, %ini%, Settings, life_cd
    }
    Else If (ctrl = vars.hwnd.settings["fm_mana_cd"])
    {
        GuiControlGet, val, , %ctrl%
        settings.flaskmanager.mana_cd := val
        IniWrite, %val%, %ini%, Settings, mana_cd
    }
    Else If (ctrl = vars.hwnd.settings["fm_cal_life"])
        Flaskmanager_Calibrate("life")
    Else If (ctrl = vars.hwnd.settings["fm_cal_mana"])
        Flaskmanager_Calibrate("mana")
    Else If (ctrl = vars.hwnd.settings["fm_tolerance"])
    {
        GuiControlGet, val, , %ctrl%
        settings.flaskmanager.tolerance := val
        IniWrite, %val%, %ini%, Settings, tolerance
        GuiControl, , % vars.hwnd.settings["fm_tol_label"], % "  " val
    }
}
