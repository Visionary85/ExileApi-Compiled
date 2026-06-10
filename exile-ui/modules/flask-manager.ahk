; =============================================================================
; Flask Manager  –  Exile-UI module
; Monitors life/mana orb pixels and auto-presses flask keys at configurable thresholds.
;
; HOW DETECTION WORKS
;   A reference pixel is sampled at a user-chosen position on the orb while the
;   orb is FULL.  During play, if that pixel's color deviates beyond the tolerance
;   value (Euclidean RGB distance), the orb has drained past the threshold position
;   and the configured flask key is sent.  A per-type cooldown prevents key spam.
;
; INTEGRATION  (three edits to existing Exile-UI files)
; -----------------------------------------------------------------------
;  Exile UI.ahk  – add at end of the #Include block:
;      #Include modules\flask-manager.ahk
;
;  Exile UI.ahk  – add in the Init sequence (after Init_screenchecks):
;      Init_flaskmanager()
;
;  modules\settings menu.ahk  – in the Settings() tab list string, append:
;      |Flask Manager
;  and in the per-tab dispatch block add:
;      Case "Flask Manager":  Settings_flaskmanager()
; =============================================================================

Init_flaskmanager()
{
    local
    global vars, settings

    settings.flaskmanager := {}
    vars.flaskmanager     := {}

    ini := "ini" vars.poe_version "\flask-manager.ini"

    settings.flaskmanager.enabled   := LLK_IniRead(ini, "Settings", "enabled",   0)
    settings.flaskmanager.use_life  := LLK_IniRead(ini, "Settings", "use_life",  0)
    settings.flaskmanager.use_mana  := LLK_IniRead(ini, "Settings", "use_mana",  0)
    settings.flaskmanager.life_key  := LLK_IniRead(ini, "Settings", "life_key",  "1")
    settings.flaskmanager.mana_key  := LLK_IniRead(ini, "Settings", "mana_key",  "2")
    settings.flaskmanager.life_cd   := LLK_IniRead(ini, "Settings", "life_cd",   500)
    settings.flaskmanager.mana_cd   := LLK_IniRead(ini, "Settings", "mana_cd",   500)
    settings.flaskmanager.tolerance := LLK_IniRead(ini, "Settings", "tolerance", 35)

    vars.flaskmanager.life_last  := 0
    vars.flaskmanager.mana_last  := 0

    vars.flaskmanager.life_x     := LLK_IniRead(ini, "Calibration", "life_x",     "")
    vars.flaskmanager.life_y     := LLK_IniRead(ini, "Calibration", "life_y",     "")
    vars.flaskmanager.life_color := LLK_IniRead(ini, "Calibration", "life_color", "")
    vars.flaskmanager.mana_x     := LLK_IniRead(ini, "Calibration", "mana_x",     "")
    vars.flaskmanager.mana_y     := LLK_IniRead(ini, "Calibration", "mana_y",     "")
    vars.flaskmanager.mana_color := LLK_IniRead(ini, "Calibration", "mana_color", "")

    If settings.flaskmanager.enabled
        SetTimer, Flaskmanager_Loop, 100
}

; ------------------------------------------------------------------
; Detection timer – 100 ms.  Only active while PoE window is focused.
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

; Euclidean distance in RGB space.  Max possible ≈ 441.
Flaskmanager_ColorDist(c1, c2)
{
    local
    r1 := (c1 >> 16) & 0xFF,  g1 := (c1 >> 8) & 0xFF,  b1 := c1 & 0xFF
    r2 := (c2 >> 16) & 0xFF,  g2 := (c2 >> 8) & 0xFF,  b2 := c2 & 0xFF
    Return Sqrt((r1-r2)**2 + (g1-g2)**2 + (b1-b2)**2)
}

; ------------------------------------------------------------------
; Master toggle – also starts/stops the detection timer.
; ------------------------------------------------------------------
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
; CALIBRATION WIZARD
; ==================================================================
; Step 1 – show the instruction dialog.  type = "life" | "mana"
Flaskmanager_Calibrate(type)
{
    local
    global vars, settings

    vars.flaskmanager.cal_type := type

    label := (type = "life") ? "Life Orb"  : "Mana Orb"
    hint  := (type = "life") ? "bottom-left" : "bottom-right"
    fs    := settings.general.fSize
    fw    := settings.general.fWidth

    Gui, fm_cal: New, -Caption +AlwaysOnTop +ToolWindow +LastFound HWNDfm_cal_hwnd
    Gui, fm_cal: Color, 111111
    Gui, fm_cal: Margin, 14, 12
    Gui, fm_cal: Font, % "s" fs+1 " cWhite Bold", % settings.general.font
    Gui, fm_cal: Add, Text, xs, % "Flask Manager  –  " label " Calibration"
    Gui, fm_cal: Font, % "s" fs " cSilver Norm", % settings.general.font
    Gui, fm_cal: Add, Text, % "xs y+8 w" fw*38
        , % "How it works`nA single pixel at your chosen orb position is sampled while the orb is full.  If that pixel's color later changes (orb drained past that point), a flask key is pressed automatically."
    Gui, fm_cal: Font, % "s" fs " cWhite", % settings.general.font
    Gui, fm_cal: Add, Text, % "xs y+8", Steps:
    Gui, fm_cal: Font, % "s" fs " cSilver", % settings.general.font
    Gui, fm_cal: Add, Text, % "xs y+4 w" fw*38
        , % "1.  Make sure your " label " is completely full`n2.  Click OK below to start the crosshair sampler`n3.  Hold Left Mouse Button over the " label " (" hint ")`n      at the HEIGHT you want as your trigger threshold`n      (lower = more aggressive;  e.g. 50 %% height = trigger at 50 %% life)`n4.  Release LMB to capture the color and save"
    Gui, fm_cal: Font, % "s" fs " cWhite Norm", % settings.general.font
    Gui, fm_cal: Add, Button, % "xs y+12 w" fw*8  " gFM_Cal_OK",     OK
    Gui, fm_cal: Add, Button, % "x+6      w" fw*8  " gFM_Cal_Cancel", Cancel
    Gui, fm_cal: Show, % "NA x" (vars.client.x + vars.client.w//2 - fw*19)
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

; Step 2 – zoom-crosshair pixel sampler (pattern mirrors Screenchecks_PixelRecalibrate2).
Flaskmanager_Calibrate2(type)
{
    local
    global vars, settings

    KeyWait, LButton, U   ; release from button click
    KeyWait, LButton, D   ; wait for user to begin sampling press

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
        MsgBox, 48, Flask Manager, Could not sample a color at that position.`nPlease try again., 5
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

; Step 3 – confirmation dialog.
Flaskmanager_CalibrationDone(label, cx, cy, color)
{
    local
    global vars, settings

    fs := settings.general.fSize
    fw := settings.general.fWidth

    Gui, fm_done: New, -Caption +AlwaysOnTop +ToolWindow +LastFound HWNDfm_done_hwnd
    Gui, fm_done: Color, 111111
    Gui, fm_done: Margin, 14, 12
    Gui, fm_done: Font, % "s" fs+1 " cLime Bold", % settings.general.font
    Gui, fm_done: Add, Text, xs, % label " calibration saved"
    Gui, fm_done: Font, % "s" fs " cSilver Norm", % settings.general.font
    Gui, fm_done: Add, Text, % "xs y+6 w" fw*30
        , % "Screen position :  " cx ",  " cy "`nReference color  :  " color "`n`nThe detection timer will now monitor this pixel."
    Gui, fm_done: Font, % "s" fs " cWhite Norm", % settings.general.font
    Gui, fm_done: Add, Button, % "xs y+10 w" fw*8 " gFM_Done_Close", OK
    Gui, fm_done: Show, % "NA x" (vars.client.x + vars.client.w//2 - fw*15)
                              . " y" (vars.client.y + vars.client.h//4)
}

FM_Done_Close:
{
    Gui, fm_done: Destroy
    Return
}

; ==================================================================
; SETTINGS TAB
; ==================================================================
Settings_flaskmanager()
{
    local
    global vars, settings, GUI

    sp := vars.settings.spacing
    fs := settings.general.fSize
    fw := settings.general.fWidth
    ini := "ini" vars.poe_version "\flask-manager.ini"

    ; ── master toggle ──────────────────────────────────────────────
    Gui, %GUI%: Add, Checkbox
        , % "xs y+" sp " Section gSettings_flaskmanager2 HWNDhwnd Checked" settings.flaskmanager.enabled
        , % " auto-flask  (life && mana)"
    vars.hwnd.settings["fm_enabled"] := hwnd

    ; ── life flask ─────────────────────────────────────────────────
    Gui, %GUI%: Font, % "s" fs " cSilver Bold"
    Gui, %GUI%: Add, Text, % "xs y+" sp*2, Life flask
    Gui, %GUI%: Font, % "s" fs " cWhite Norm"

    Gui, %GUI%: Add, Checkbox
        , % "ys gSettings_flaskmanager2 HWNDhwnd Checked" settings.flaskmanager.use_life
        , % " enabled"
    vars.hwnd.settings["fm_use_life"] := hwnd

    Gui, %GUI%: Add, Text,  % "xs y+" sp " Section", % "  key (AHK send string):"
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
        , % "xs y+" sp " gSettings_flaskmanager2 HWNDhwnd w" fw*20
        , % " Calibrate Life Orb threshold..."
    vars.hwnd.settings["fm_cal_life"] := hwnd

    If !Blank(vars.flaskmanager.life_color)
    {
        Gui, %GUI%: Font, % "s" fs-1 " c00CC66"
        Gui, %GUI%: Add, Text, % "xs y+2"
            , % "  calibrated  –  pos: " vars.flaskmanager.life_x ", " vars.flaskmanager.life_y
              . "   color: " vars.flaskmanager.life_color
    }
    Else
    {
        Gui, %GUI%: Font, % "s" fs-1 " cFFAA00"
        Gui, %GUI%: Add, Text, % "xs y+2", % "  not yet calibrated – click button above to set threshold"
    }
    Gui, %GUI%: Font, % "s" fs " cWhite Norm"

    ; ── mana flask ─────────────────────────────────────────────────
    Gui, %GUI%: Font, % "s" fs " cSilver Bold"
    Gui, %GUI%: Add, Text, % "xs y+" sp*2, Mana flask
    Gui, %GUI%: Font, % "s" fs " cWhite Norm"

    Gui, %GUI%: Add, Checkbox
        , % "ys gSettings_flaskmanager2 HWNDhwnd Checked" settings.flaskmanager.use_mana
        , % " enabled"
    vars.hwnd.settings["fm_use_mana"] := hwnd

    Gui, %GUI%: Add, Text,  % "xs y+" sp " Section", % "  key (AHK send string):"
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
        , % "xs y+" sp " gSettings_flaskmanager2 HWNDhwnd w" fw*20
        , % " Calibrate Mana Orb threshold..."
    vars.hwnd.settings["fm_cal_mana"] := hwnd

    If !Blank(vars.flaskmanager.mana_color)
    {
        Gui, %GUI%: Font, % "s" fs-1 " c00CC66"
        Gui, %GUI%: Add, Text, % "xs y+2"
            , % "  calibrated  –  pos: " vars.flaskmanager.mana_x ", " vars.flaskmanager.mana_y
              . "   color: " vars.flaskmanager.mana_color
    }
    Else
    {
        Gui, %GUI%: Font, % "s" fs-1 " cFFAA00"
        Gui, %GUI%: Add, Text, % "xs y+2", % "  not yet calibrated – click button above to set threshold"
    }
    Gui, %GUI%: Font, % "s" fs " cWhite Norm"

    ; ── color tolerance ────────────────────────────────────────────
    Gui, %GUI%: Font, % "s" fs " cSilver"
    Gui, %GUI%: Add, Text, % "xs y+" sp*2 " Section", % "color tolerance  (10 – 100):"
    Gui, %GUI%: Font, % "s" fs " cWhite"
    Gui, %GUI%: Add, Slider
        , % "ys gSettings_flaskmanager2 HWNDhwnd w" fw*20 " Range10-100 TickInterval10 NoTicks"
        , % settings.flaskmanager.tolerance
    vars.hwnd.settings["fm_tolerance"] := hwnd
    Gui, %GUI%: Add, Text, % "ys hp HWNDhwnd w" fw*5, % "  " settings.flaskmanager.tolerance
    vars.hwnd.settings["fm_tol_label"] := hwnd

    ; ── help note ──────────────────────────────────────────────────
    Gui, %GUI%: Font, % "s" fs-1 " cSilver"
    Gui, %GUI%: Add, Text, % "xs y+" sp*2 " w" fw*38
        , % "Tolerance: how different the sampled pixel color must be from the reference before "
          . "triggering (Euclidean RGB distance, max ~441).  Lower = more sensitive.  "
          . "Raise if false-triggers occur; lower if flasks are late."
    Gui, %GUI%: Font, % "s" fs " cWhite Norm"
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
