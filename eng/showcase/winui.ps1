# A story (eng/showcase/record.ps1 plays it): the "WinUI 3" tab of the home page's app models. A WinUI 3 page's XAML
# gets a TextBlock bound with x:Bind, its path completed from the page's view model, and the app runs with it.

function Prepare {
    $app = "$Work\src\WinUIApp"
    New-Item -ItemType Directory -Force $app | Out-Null
    $desktop = Join-Path $Root 'samples/WindowsDesktop'
    Copy-Item -Recurse "$desktop/WinUIApp" "$app/WinUIApp"
    Copy-Item "$desktop/WinUIApp.slnx", "$desktop/Directory.Build.props" $app
    $script:Open = @("$app\WinUIApp.slnx", '--open', "$app\WinUIApp\MainPage.xaml")
    $script:Load = 60
}

function Setup {
    Click 973 589; Wait 0.5; Click 973 589          # the Error List and Output close
    Click 1257 82; Wait 0.5                          # the Solution Explorer closes: the editor gets the width
    Key ctrl+shift+b; Wait 60                        # a first build, so F5 starts straight away
    Click 1256 631; Wait 0.5                         # the build's Output closes
    Click 600 300; Scroll -4                         # the editor scrolled to line 14 (y 166)
}

function Story {
    Wait 0.5
    Mark start
    Wait 1
    # A TextBlock after the rating, bound with x:Bind: the page's view model's members complete.
    Click 700 587; Rest; Key End; Wait 0.4; Key Return; Wait 0.3
    Typed 110 '<TextBl'; Wait 0.8; Typed 110 'ock'; Key Escape
    Typed 110 ' Text'; Key Escape; Typed 1 '='; Key Escape; Typed 1 '"'; Key Escape
    Typed 110 '{x:Bind ViewModel.'; Wait 2
    Typed 160 'Ra'; Wait 0.8; Key Tab; Wait 0.4
    Typed 110 '}'; Key Escape; Typed 1 '"'; Key Escape
    Typed 70 ' VerticalAlignment'; Key Escape; Typed 1 '='; Key Escape; Typed 1 '"'; Wait 0.8
    Typed 90 'Center'; Key Escape; Typed 1 '"'; Key Escape; Typed 80 ' />'; Key Escape
    Wait 1
    # Saved and run: the app's window, beside the XAML, and the rating moved shows in the new text.
    Key ctrl+s; Wait 0.5
    Key F5
    $deadline = (Get-Date).AddSeconds(90)
    do {
        Start-Sleep -Milliseconds 200
        $run = Get-Process WinUIApp -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } | Select-Object -First 1
    } until ($run -or (Get-Date) -gt $deadline)
    if (-not $run) { throw "the app didn't open its window" }
    [void][CodalusShowcase]::MoveWindow($run.MainWindowHandle, 560, 300, 640, 420, $true)
    [void][CodalusShowcase]::SetForegroundWindow($run.MainWindowHandle)
    Wait 2
    Click 750 684; Wait 1.2                          # the rating to 5, and the new text with it
    Click 606 403                                    # a to-do done
    Rest; Wait 2.5
    Mark end
}
