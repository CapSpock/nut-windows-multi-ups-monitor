Imports System.IO
Imports System.Net.Sockets
Imports System.Text
Imports System.Windows.Forms
Imports System.Drawing
Imports System.Drawing.Drawing2D

Public Class Form1
    Private Shared lockStream As System.IO.FileStream

    Public Sub New()
        Try
            ' Ablage im absolut neutralen Windows-Temp-Verzeichnis (garantiert keine UAC-Virtualisierung)
            Dim lockFilePath As String = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TMD_NUT_Client_Monitor.lock")

            ' Exklusive Sperre öffnen
            lockStream = New System.IO.FileStream(lockFilePath, System.IO.FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None)
        Catch ex As Exception
            ' Wenn die Datei bereits gesperrt ist -> sofortiger Abbruch
            Environment.Exit(0)
            Return
        End Try

        InitializeComponent()
    End Sub

    Private WithEvents timerPoll As New Timer()
    Private serverIp As String = "192.168.1.100"
    Private serverPort As Integer = 3493
    Private pollIntervalMs As Integer = 5000
    Private shutdownThreshold As Integer = 15
    Private shutdownEnabled As Boolean = True

    Private ReadOnly upsList As New List(Of UpsConfig)()
    Private ReadOnly TabControl1 As New TabControl()

    Private WithEvents trayIcon As New NotifyIcon()
    Private ReadOnly trayMenu As New ContextMenuStrip()

    Private Class UpsConfig
        Public Property Name As String
        Public Property Title As String
        Public Property TabPage As TabPage
        Public Property LblStatus As Label
        Public Property LblModel As Label
        Public Property LblSerial As Label
        Public Property LblVoltage As Label
        Public Property LblCharge As Label
        Public Property LblBattVoltage As Label
        Public Property LblRuntime As Label
        Public Property LblLoad As Label
        Public Property LblBattDate As Label
        Public Property LblBeeper As Label
        Public Property BatteryPanel As Panel
        Public Property CurrentCharge As Integer = 100
    End Class

    Private Shared appMutex As System.Threading.Mutex = Nothing

    Private Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load

        EnsureAutoStart() ' Automatischer Windows-Start sicherstellen
        LoadConfiguration()

        Me.Text = $"NUT Monitor | IP: {serverIp} | Port: {serverPort} | USVen: {upsList.Count}"
        Me.Width = 550
        Me.Height = 350

        ' --- Icon fehlerfrei für Fenster und System-Tray laden ---
        Try
            Dim iconPath As String = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logo.ico")

            If System.IO.File.Exists(iconPath) Then
                ' 1. Hauptfenster bekommt das originale Icon
                Dim baseIcon As New Icon(iconPath)
                Me.Icon = baseIcon

                ' 2. Für den System-Tray erzwingen wir ein sauberes 32x32-Bitmap/Icon, 
                '    damit Windows niemals den Platzhalter anzeigt!
                Using bmp As New Bitmap(baseIcon.ToBitmap(), 16, 16)
                    trayIcon.Icon = Icon.FromHandle(bmp.GetHicon())
                End Using
            Else
                ' Fallback auf Standard, falls Datei fehlt
                Me.Icon = SystemIcons.Application
                trayIcon.Icon = SystemIcons.Application
            End If
        Catch ex As Exception
            Me.Icon = SystemIcons.Application
            trayIcon.Icon = SystemIcons.Application
        End Try

        BuildDynamicUI()
        SetupTrayIcon()

        ' Prüfen, ob das Tool mit dem Parameter /tray gestartet wurde (Autostart)
        Dim args As String() = Environment.GetCommandLineArgs()
        If args.Contains("/tray") Then
            Me.WindowState = FormWindowState.Minimized
            Me.ShowInTaskbar = False
            Me.Hide()
            trayIcon.Visible = True
        End If

        timerPoll.Interval = pollIntervalMs
        timerPoll.Start()

        BeginInvoke(Sub() PollAllUpsData())
    End Sub

    Private Sub SetupTrayIcon()
        Try
            Dim iconPath As String = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logo.ico")

            If System.IO.File.Exists(iconPath) Then
                ' 1. Das Hauptfenster bekommt die normale ICO-Datei
                Me.Icon = New Icon(iconPath)

                ' 2. Für den System-Tray zeichnen wir ein frisches, perfektes 16x16 32-Bit Icon im Speicher
                Using loadedImg As Image = Image.FromFile(iconPath)
                    Dim canvas As New Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppPArgb)
                    Using g As Graphics = Graphics.FromImage(canvas)
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality
                        g.DrawImage(loadedImg, New Rectangle(0, 0, 16, 16))
                    End Using

                    ' Erzeuge ein echtes Windows-Icon-Handle (HICON)
                    Dim hIcon As IntPtr = canvas.GetHicon()
                    trayIcon.Icon = Icon.FromHandle(hIcon)
                End Using
            Else
                Me.Icon = SystemIcons.Application
                trayIcon.Icon = SystemIcons.Application
            End If
        Catch ex As Exception
            Me.Icon = SystemIcons.Application
            trayIcon.Icon = SystemIcons.Application
        End Try
    End Sub

    Private Sub TrayIcon_DoubleClick(sender As Object, e As EventArgs) Handles trayIcon.DoubleClick
        RestoreWindow()
    End Sub

    Private Sub RestoreWindow()
        Me.Show()
        Me.WindowState = FormWindowState.Normal
        Me.BringToFront()
    End Sub

    Private Sub ExitApplication()
        trayIcon.Visible = False
        Application.Exit()
    End Sub

    Private Sub Form1_Resize(sender As Object, e As EventArgs) Handles MyBase.Resize
        If Me.WindowState = FormWindowState.Minimized Then
            Me.Hide()
            trayIcon.Visible = True
        End If
    End Sub

    Private Sub Form1_FormClosing(sender As Object, e As FormClosingEventArgs) Handles MyBase.FormClosing
        Dim result As DialogResult = MessageBox.Show(
            "Möchten Sie das Programm wirklich komplett beenden?" & vbCrLf & vbCrLf &
            "Klicken Sie auf 'Ja' zum Beenden oder auf 'Nein', um das Tool in die Taskleiste zu minimieren.",
            "Programm beenden?",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question)

        If result = DialogResult.Yes Then
            trayIcon.Visible = False
        ElseIf result = DialogResult.No Then
            e.Cancel = True
            Me.WindowState = FormWindowState.Minimized
            Me.Hide()
        Else
            e.Cancel = True
        End If
    End Sub

    Private Sub LoadConfiguration()
        Dim iniPath As String = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini")

        If File.Exists(iniPath) Then
            Dim lines = File.ReadAllLines(iniPath)
            Dim currentSection As String = ""
            Dim currentName As String = ""
            Dim currentTitle As String = ""

            For Each line In lines
                line = line.Trim()
                If line.StartsWith(";") OrElse line.StartsWith("#") OrElse String.IsNullOrEmpty(line) Then Continue For

                If line.StartsWith("[") AndAlso line.EndsWith("]") Then
                    If currentSection.StartsWith("UPS_") AndAlso Not String.IsNullOrEmpty(currentName) Then
                        upsList.Add(New UpsConfig With {.Name = currentName, .Title = If(String.IsNullOrEmpty(currentTitle), currentName, currentTitle)})
                        currentName = ""
                        currentTitle = ""
                    End If
                    currentSection = line.Substring(1, line.Length - 2).Trim().ToUpper()
                    Continue For
                End If

                Dim parts = line.Split("="c)
                If parts.Length >= 2 Then
                    Dim key = parts(0).Trim().ToLower()
                    Dim val = String.Join("=", parts, 1, parts.Length - 1).Trim()

                    If currentSection = "GENERAL" Then
                        Select Case key
                            Case "serverip" : If Not String.IsNullOrEmpty(val) Then serverIp = val
                            Case "port" : Integer.TryParse(val, serverPort)
                            Case "intervalseconds"
                                Dim sec As Integer
                                If Integer.TryParse(val, sec) Then pollIntervalMs = sec * 1000
                            Case "shutdownthresholdpercent" : Integer.TryParse(val, shutdownThreshold)
                            Case "shutdownenabled" : Boolean.TryParse(val, shutdownEnabled)
                        End Select
                    ElseIf currentSection.StartsWith("UPS_") Then
                        Select Case key
                            Case "name" : currentName = val
                            Case "title" : currentTitle = val
                        End Select
                    End If
                End If
            Next

            If currentSection.StartsWith("UPS_") AndAlso Not String.IsNullOrEmpty(currentName) Then
                upsList.Add(New UpsConfig With {.Name = currentName, .Title = If(String.IsNullOrEmpty(currentTitle), currentName, currentTitle)})
            End If
        End If

        If upsList.Count = 0 Then
            upsList.Add(New UpsConfig With {.Name = "usv_links", .Title = "USV Links"})
            upsList.Add(New UpsConfig With {.Name = "usv_rechts", .Title = "USV Rechts"})
        End If
    End Sub

    Private Sub BuildDynamicUI()
        TabControl1.Dock = DockStyle.Fill
        Me.Controls.Add(TabControl1)

        For Each ups In upsList
            Dim tp As New TabPage(ups.Title)

            Dim splitLayout As New TableLayoutPanel With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 2,
                .RowCount = 1
            }
            splitLayout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 70.0F))
            splitLayout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 30.0F))

            Dim panGrid As New TableLayoutPanel With {
                .Dock = DockStyle.Fill,
                .ColumnCount = 2,
                .RowCount = 10,
                .Padding = New Padding(15)
            }

            Dim rowIdx As Integer = 0

            ' Hilfsfunktion inline als Lambda für saubere Zeilen
            Dim addField = Sub(labelTxt As String, ByRef targetLbl As Label)
                               panGrid.Controls.Add(New Label With {.Text = labelTxt, .Font = New Font(Me.Font, FontStyle.Bold), .AutoSize = True}, 0, rowIdx)
                               targetLbl = New Label With {.Text = "---", .AutoSize = True}
                               panGrid.Controls.Add(targetLbl, 1, rowIdx)
                               rowIdx += 1
                           End Sub

            addField("Status:", ups.LblStatus)
            addField("Modell:", ups.LblModel)
            addField("Seriennummer:", ups.LblSerial)
            addField("Netzspannung:", ups.LblVoltage)
            addField("Akkuladung:", ups.LblCharge)
            addField("Akkuspannung:", ups.LblBattVoltage)
            addField("Restlaufzeit:", ups.LblRuntime)
            addField("Auslastung:", ups.LblLoad)
            addField("Batteriedatum:", ups.LblBattDate)
            addField("Beeper:", ups.LblBeeper)

            Dim rightPanel As New Panel With {
                .Dock = DockStyle.Fill,
                .Padding = New Padding(20)
            }

            ups.BatteryPanel = New Panel With {
                .Width = 120,
                .Height = 240,
                .Location = New Point(40, 40)
            }
            AddHandler ups.BatteryPanel.Paint, AddressOf BatteryPanel_Paint
            rightPanel.Controls.Add(ups.BatteryPanel)

            splitLayout.Controls.Add(panGrid, 0, 0)
            splitLayout.Controls.Add(rightPanel, 1, 0)

            tp.Controls.Add(splitLayout)
            TabControl1.TabPages.Add(tp)
            ups.TabPage = tp
        Next

        ' --- Info-Tab mit Firmen- und Entwicklerdaten inklusive Logo-Banner ---
        Dim tpInfo As New TabPage("Info")

        Dim infoScroll As New FlowLayoutPanel With {
            .Dock = DockStyle.Fill,
            .FlowDirection = FlowDirection.TopDown,
            .WrapContents = False,
            .AutoScroll = True,
            .Padding = New Padding(20)
        }

        ' --- Info-Tab mit Firmen- und Entwicklerdaten (Sauber scrollbar über TableLayoutPanel) ---

        Dim infoPanel As New Panel With {
            .Dock = DockStyle.Fill,
            .AutoScroll = True
        }

        Dim infoLayout As New TableLayoutPanel With {
            .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .ColumnCount = 1,
            .RowCount = 8,
            .Dock = DockStyle.Top,
            .Padding = New Padding(20)
        }

        ' --- Config-Tab zum Bearbeiten der config.ini ---
        Dim tpConfig As New TabPage("Konfiguration")

        Dim configLayout As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 3,
            .Padding = New Padding(15)
        }
        configLayout.RowStyles.Add(New RowStyle(SizeType.Absolute, 35.0F))
        configLayout.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        configLayout.RowStyles.Add(New RowStyle(SizeType.Absolute, 50.0F))

        Dim lblConfigHint As New Label With {
            .Text = "Hier können Sie die config.ini direkt anpassen und abspeichern (wirkt beim nächsten Polling bzw. Neustart):",
            .Dock = DockStyle.Fill,
            .TextAlign = ContentAlignment.MiddleLeft
        }
        configLayout.Controls.Add(lblConfigHint, 0, 0)

        Dim txtConfigContent As New TextBox With {
            .Multiline = True,
            .ScrollBars = ScrollBars.Both,
            .Dock = DockStyle.Fill,
            .Font = New Font("Consolas", 10, FontStyle.Regular)
        }

        ' config.ini einlesen oder Standardtemplate erzeugen
        Dim iniPath As String = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini")
        If File.Exists(iniPath) Then
            txtConfigContent.Text = File.ReadAllText(iniPath)
        Else
            txtConfigContent.Text =
                "; --- NUT Client Konfiguration ---" & vbCrLf &
                "[GENERAL]" & vbCrLf &
                "ServerIp = 192.168.1.100" & vbCrLf &
                "Port = 3493" & vbCrLf &
                "IntervalSeconds = 5" & vbCrLf &
                "ShutdownThresholdPercent = 15" & vbCrLf &
                "ShutdownEnabled = True" & vbCrLf &
                vbCrLf &
                "[UPS_Links]" & vbCrLf &
                "Name = usv_links" & vbCrLf &
                "Title = USV Links" & vbCrLf &
                vbCrLf &
                "[UPS_Rechts]" & vbCrLf &
                "Name = usv_rechts" & vbCrLf &
                "Title = USV Rechts"
        End If
        configLayout.Controls.Add(txtConfigContent, 0, 1)

        Dim btnSaveConfig As New Button With {
            .Text = "Konfiguration speichern",
            .Width = 180,
            .Height = 35,
            .Anchor = AnchorStyles.Right
        }

        AddHandler btnSaveConfig.Click, Sub(s, ev)
                                            Try
                                                File.WriteAllText(iniPath, txtConfigContent.Text)
                                                MessageBox.Show("Die config.ini wurde erfolgreich gespeichert!", "Erfolg", MessageBoxButtons.OK, MessageBoxIcon.Information)

                                                ' USV-Liste und Tabs komplett zurücksetzen und UI neu aufbauen
                                                upsList.Clear()
                                                TabControl1.TabPages.Clear()
                                                LoadConfiguration()
                                                BuildDynamicUI()
                                            Catch ex As Exception
                                                MessageBox.Show($"Fehler beim Speichern: {ex.Message}", "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error)
                                            End Try
                                        End Sub

        configLayout.Controls.Add(btnSaveConfig, 0, 2)
        tpConfig.Controls.Add(configLayout)
        TabControl1.TabPages.Add(tpConfig)

        ' Logo-Banner einbinden (falls vorhanden)
        Dim imgPath As String = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logo.jpg")
        If File.Exists(imgPath) Then
            Dim picLogo As New PictureBox With {
                .Image = Image.FromFile(imgPath),
                .SizeMode = PictureBoxSizeMode.Zoom,
                .Width = 450,
                .Height = 220,
                .Margin = New Padding(0, 0, 0, 15)
            }
            infoLayout.Controls.Add(picLogo, 0, 0)
        End If

        Dim rowCountIdx As Integer = 1
        Dim addInfoLine = Sub(text As String, font As Font, color As Color, spaceBottom As Integer)
                              Dim lbl As New Label With {
                                  .Text = text,
                                  .Font = font,
                                  .ForeColor = color,
                                  .AutoSize = True,
                                  .Margin = New Padding(0, 0, 0, spaceBottom)
                              }
                              infoLayout.Controls.Add(lbl, 0, rowCountIdx)
                              rowCountIdx += 1
                          End Sub

        addInfoLine("NUT Multi-USV Redundanz-Monitor", New Font(Me.Font.FontFamily, 14, FontStyle.Bold), Color.Black, 5)
        addInfoLine("Hochverfügbares .NET Überwachungstool für NUT-Netzwerke", Me.Font, Color.DimGray, 15)
        addInfoLine("TMD-Technik GbR" & vbCrLf & "Tobias Putschli und Dominik Müller" & vbCrLf & "Pleißenaue 3-5" & vbCrLf & "08451 Crimmitschau/Sa.", New Font(Me.Font, FontStyle.Bold), Color.Black, 10)
        addInfoLine("Tel.: 03762/914410", Me.Font, Color.Black, 5)
        addInfoLine("Web: www.TMD-Technik.de", Me.Font, Color.Blue, 15)
        addInfoLine("Zielumgebung: Windows Server 2022 / .NET 8/9", Me.Font, Color.Gray, 5)
        addInfoLine("Version 1.0 (Produktionsbetrieb)", Me.Font, Color.Gray, 10)

        infoPanel.Controls.Add(infoLayout)
        tpInfo.Controls.Add(infoPanel)
        TabControl1.TabPages.Add(tpInfo)

    End Sub

    Private Sub BatteryPanel_Paint(sender As Object, e As PaintEventArgs)
        Dim pnl = CType(sender, Panel)
        Dim g = e.Graphics
        g.SmoothingMode = SmoothingMode.AntiAlias

        Dim currentUps = upsList.FirstOrDefault(Function(u) u.BatteryPanel Is pnl)
        Dim charge = If(currentUps IsNot Nothing, currentUps.CurrentCharge, 100)

        Dim battRect As New Rectangle(20, 30, 80, 180)
        Dim pinRect As New Rectangle(50, 15, 20, 15)

        g.DrawRectangle(Pens.Black, battRect)
        g.DrawRectangle(Pens.Black, pinRect)
        g.FillRectangle(Brushes.DarkGray, pinRect)

        Dim fillHeight = CInt((battRect.Height - 4) * (charge / 100.0))
        Dim fillY = battRect.Y + battRect.Height - 2 - fillHeight
        Dim fillRect As New Rectangle(battRect.X + 2, fillY, battRect.Width - 4, fillHeight)

        Dim fillBrush As Brush = Brushes.Green
        If charge <= 20 Then
            fillBrush = Brushes.Red
        ElseIf charge <= 50 Then
            fillBrush = Brushes.Orange
        End If

        g.FillRectangle(fillBrush, fillRect)

        Dim textString = $"{charge}%"
        Dim sf As New StringFormat With {.Alignment = StringAlignment.Center, .LineAlignment = StringAlignment.Center}
        g.DrawString(textString, New Font(Me.Font.FontFamily, 12, FontStyle.Bold), Brushes.Black, battRect, sf)
    End Sub

    Private isPolling As Boolean = False

    Private Sub TimerPoll_Tick(sender As Object, e As EventArgs) Handles timerPoll.Tick
        ' Verhindert, dass sich Poll-Vorgänge stapeln, falls der Pi mal länger braucht
        If isPolling Then Return

        Task.Run(Sub()
                     isPolling = True
                     PollAllUpsData()
                     isPolling = False
                 End Sub)
    End Sub
    Private Sub PollAllUpsData()
        Dim allDataCollected As Boolean = True
        Dim upsStatusList As New Dictionary(Of String, Dictionary(Of String, String))()

        For Each ups In upsList
            Dim errorMsg As String = ""
            Dim data = GetNutVariables(ups.Name, errorMsg)
            upsStatusList(ups.Name) = data

            If data.Count > 0 Then
                Dim status As String = "" : If Not data.TryGetValue("ups.status", status) Then status = "Unbekannt"
                Dim model As String = "" : If Not data.TryGetValue("device.model", model) Then model = "Unbekannt"
                Dim serial As String = "" : If Not data.TryGetValue("device.serial", serial) Then serial = "---"
                Dim voltage As String = "" : If Not data.TryGetValue("input.voltage", voltage) Then voltage = "0"
                Dim chargeStr As String = "" : If Not data.TryGetValue("battery.charge", chargeStr) Then chargeStr = "100"
                Dim battVolt As String = "" : If Not data.TryGetValue("battery.voltage", battVolt) Then battVolt = "0"
                Dim runtime As String = "" : If Not data.TryGetValue("battery.runtime", runtime) Then runtime = "0"
                Dim load As String = "" : If Not data.TryGetValue("ups.load", load) Then load = "0"
                Dim battDate As String = "" : If Not data.TryGetValue("battery.date", battDate) Then
                    If Not data.TryGetValue("battery.mfr.date", battDate) Then battDate = "Unbekannt"
                End If
                Dim beeper As String = "" : If Not data.TryGetValue("ups.beeper.status", beeper) Then beeper = "Unbekannt"

                Dim chargeVal As Integer = 100
                Integer.TryParse(chargeStr, chargeVal)
                ups.CurrentCharge = chargeVal

                Invoke(Sub()
                           If status.Contains("OL") Then
                               ups.LblStatus.Text = "Online (Netzbetrieb)"
                               ups.LblStatus.ForeColor = Color.Green
                           ElseIf status.Contains("OB") Then
                               ups.LblStatus.Text = "Batteriebetrieb (Achtung!)"
                               ups.LblStatus.ForeColor = Color.Red
                           Else
                               ups.LblStatus.Text = status
                               ups.LblStatus.ForeColor = Color.DarkOrange
                           End If

                           ups.LblModel.Text = model
                           ups.LblSerial.Text = serial
                           ups.LblVoltage.Text = voltage & " V"
                           ups.LblCharge.Text = chargeStr & " %"
                           ups.LblBattVoltage.Text = battVolt & " V"
                           ups.LblRuntime.Text = runtime & " Sek."
                           ups.LblLoad.Text = load & " %"
                           ups.LblBattDate.Text = battDate
                           ups.LblBeeper.Text = beeper

                           If ups.BatteryPanel IsNot Nothing Then
                               ups.BatteryPanel.Invalidate()
                           End If
                       End Sub)
            Else
                allDataCollected = False
                Dim currentError = errorMsg
                Invoke(Sub()
                           ups.LblStatus.Text = $"Fehler: {currentError}"
                           ups.LblStatus.ForeColor = Color.DarkRed
                       End Sub)
            End If
        Next

        If shutdownEnabled AndAlso upsStatusList.Count > 0 Then
            EvaluateRedundantShutdown(upsStatusList)
        End If
    End Sub

    Private Sub EvaluateRedundantShutdown(statusDict As Dictionary(Of String, Dictionary(Of String, String)))
        Dim totalUpsCount As Integer = upsList.Count
        Dim reportingUpsCount As Integer = statusDict.Count
        Dim hasCommunicationError As Boolean = (reportingUpsCount < totalUpsCount)

        Dim allOnBattery As Boolean = True
        Dim criticalBatteryFound As Boolean = False
        Dim anyOnBattery As Boolean = False

        For Each kvp In statusDict
            Dim data = kvp.Value
            Dim status As String = "" : If Not data.TryGetValue("ups.status", status) Then status = ""
            Dim chargeStr As String = "" : If Not data.TryGetValue("battery.charge", chargeStr) Then chargeStr = "100"

            Dim charge As Integer = 100
            Integer.TryParse(chargeStr, charge)

            If status.Contains("OB") Then
                anyOnBattery = True
            Else
                allOnBattery = False
            End If

            If charge <= shutdownThreshold Then
                criticalBatteryFound = True
            End If
        Next

        ' Szenario A: Alle USVen antworten, alle auf Batterie UND mind. eine kritisch
        Dim conditionAllRedundant As Boolean = (Not hasCommunicationError) AndAlso allOnBattery AndAlso criticalBatteryFound

        ' Szenario B: Kommunikationsfehler bei mindestens einer USV, aber die verbleibenden 
        '             laufen auf Batterie und sind kritisch (Sicherheits-Fallback bei Redundanzverlust)
        Dim conditionPartialFailure As Boolean = hasCommunicationError AndAlso anyOnBattery AndAlso criticalBatteryFound

        If conditionAllRedundant OrElse conditionPartialFailure Then
            timerPoll.Stop()
            Try
                If conditionAllRedundant OrElse conditionPartialFailure Then
                    timerPoll.Stop()

                    Dim reasonText = If(hasCommunicationError,
                        "Redundanzverlust (USV-Ausfall/Kommunikationsfehler) + verbleibende USV auf Batterie/kritisch!",
                        "Gesamtausfall aller USV-Quellen: Akkus kritisch.")

                    ' 1. Warnfenster entkoppelt im Haupt-UI-Thread (oder neuem Thread) öffnen, ohne zu blockieren
                    Invoke(Sub()
                               Dim warnDlg As New ShutdownWarningForm(reasonText)
                               warnDlg.Show() ' .Show() statt .ShowDialog(), damit es nicht blockiert!
                               warnDlg.BringToFront()
                           End Sub)

                    ' 2. Betriebssystem-Shutdown sofort auslösen
                    Try
                        Process.Start("shutdown.exe", $"/s /t 10 /c ""{reasonText} Server wird heruntergefahren.""")
                    Catch
                        ' Ignorieren
                    End Try
                End If
            Catch
                ' Ignorieren
            End Try
        End If
    End Sub

    Private Function GetNutVariables(upsName As String, ByRef errorMessage As String) As Dictionary(Of String, String)
        Dim result As New Dictionary(Of String, String)()
        errorMessage = "Unbekannter Fehler"

        Try
            Using client As New TcpClient()
                ' Verbindung asynchron mit 2 Sekunden Timeout aufbauen
                Dim connectTask = client.ConnectAsync(serverIp, serverPort)
                If Not connectTask.Wait(2000) Then
                    errorMessage = "Timeout (Verbindung zum Pi nicht erreichbar)"
                    Return result
                End If

                Using stream As NetworkStream = client.GetStream()
                    stream.ReadTimeout = 2000
                    stream.WriteTimeout = 2000

                    Using writer As New StreamWriter(stream, Encoding.ASCII) With {.AutoFlush = True}
                        Using reader As New StreamReader(stream, Encoding.ASCII)
                            writer.WriteLine($"LIST VAR {upsName}")
                            While True
                                Dim line = reader.ReadLine()
                                If String.IsNullOrEmpty(line) Then Exit While
                                If line.StartsWith("ERR") Then
                                    errorMessage = $"NUT-Fehler: {line}"
                                    Exit While
                                End If
                                If line.StartsWith($"BEGIN LIST VAR {upsName}") Then Continue While
                                If line.StartsWith($"END LIST VAR {upsName}") Then Exit While

                                Dim firstQuote = line.IndexOf(""""c)
                                Dim lastQuote = line.LastIndexOf(""""c)
                                If firstQuote > -1 AndAlso lastQuote > firstQuote Then
                                    Dim val = line.Substring(firstQuote + 1, lastQuote - firstQuote - 1)
                                    Dim header = line.Substring(0, firstQuote).Trim()
                                    Dim tokens = header.Split(" "c)
                                    If tokens.Length >= 3 Then
                                        result(tokens(2)) = val
                                    End If
                                End If
                            End While
                        End Using
                    End Using
                End Using
            End Using
        Catch ex As Exception
            errorMessage = ex.Message
        End Try
        Return result
    End Function

    Private Sub EnsureAutoStart()
        Try
            Const appName As String = "NutClientMonitor"
            Dim runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("SOFTWARE\Microsoft\Windows\CurrentVersion\Run", True)

            If runKey IsNot Nothing Then
                Dim existingValue = runKey.GetValue(appName)

                ' Nur fragen, wenn der Eintrag noch NICHT existiert
                If existingValue Is Nothing Then
                    Dim result As DialogResult = MessageBox.Show(
                        "Möchten Sie, dass dieses Tool automatisch beim Start von Windows ausgeführt wird?",
                        "Autostart einrichten",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question)

                    If result = DialogResult.Yes Then
                        Dim exePath As String = Application.ExecutablePath
                        runKey.SetValue(appName, """" & exePath & """ /tray")
                    End If
                End If
            End If
        Catch
            ' Fehler beim Registry-Zugriff abfangen
        End Try
    End Sub
End Class
Public Class ShutdownWarningForm
    Inherits Form

    Public Sub New(reason As String)
        Me.Text = "CRITICAL WARNING: USV NOT-SHUTDOWN"
        Me.Size = New Size(550, 260)
        Me.StartPosition = FormStartPosition.CenterScreen
        Me.FormBorderStyle = FormBorderStyle.FixedDialog
        Me.MaximizeBox = False
        Me.MinimizeBox = False
        Me.TopMost = True ' Bleibt immer im Vordergrund
        Me.BackColor = Color.DarkRed

        Dim lblTitle As New Label With {
            .Text = "SERVER WIRD HERUNTERGEFAHREN!",
            .Font = New Font("Segoe UI", 16, FontStyle.Bold),
            .ForeColor = Color.White,
            .Dock = DockStyle.Top,
            .Height = 50,
            .TextAlign = ContentAlignment.MiddleCenter
        }

        Dim lblMessage As New Label With {
            .Text = $"Auslöser:" & vbCrLf & reason & vbCrLf & vbCrLf & "Der Server fährt in ca. 30 Sekunden herunter. Bitte offene Arbeiten sichern!",
            .Font = New Font("Segoe UI", 11, FontStyle.Regular),
            .ForeColor = Color.Yellow,
            .Dock = DockStyle.Fill,
            .TextAlign = ContentAlignment.MiddleCenter,
            .Padding = New Padding(15)
        }

        Me.Controls.Add(lblMessage)
        Me.Controls.Add(lblTitle)
    End Sub
End Class