# A story (eng/showcase/record.ps1 plays it): the "WPF" tab of the home page's app models. A WPF window's XAML is edited
# with completion while the WPF XAML Previewer draws it, and a click in the preview selects the element in the XAML.

function Prepare {
    $app = "$Work\src\WpfApp"
    New-Item -ItemType Directory -Force $app | Out-Null
    $desktop = Join-Path $Root 'samples/WindowsDesktop'
    Copy-Item -Recurse "$desktop/WpfApp" "$app/WpfApp"
    Copy-Item "$desktop/WpfApp.slnx", "$desktop/Directory.Build.props" $app
    $script:Open = @("$app\WpfApp.slnx", '--open', "$app\WpfApp\MainWindow.xaml")
    $script:Load = 40
}

function Setup {
    Click 973 589; Wait 0.5; Click 973 589          # the Error List and Output close: the editor gets the height
    Key shift+F7                                     # the XAML Previewer, which builds the app first
    Wait 75
    Click 1060 16                                    # the shell in front
    Drag 993 300 640 300                             # the previewer gets half the window
    Click 300 400; Scroll -4                         # the editor scrolled to line 14 (y 166)
    Wait 1
}

# The editor's line n (y), 17.5 pixels a line from line 14.
function Line([int] $N) { [int](166 + ($N - 14) * 17.5) }

function Story {
    Wait 0.5
    Mark start
    Wait 1
    # The button's text.
    Click 500 (Line 28) -Double; Wait 0.6
    Typed 110 'Add a to-do'; Key Escape
    Rest; Wait 1.8

    # The list framed and on a light green, the colors typed with completion.
    Click 300 (Line 31); Key Home; for ($i = 0; $i -lt 8; $i++) { Key Right }; Wait 0.4
    Typed 130 ' Bor'; Wait 1.2; Typed 120 'derBrush'; Key Escape
    Typed 1 '='; Key Escape; Typed 1 '"'; Wait 1.2; Typed 180 'SeaGreen'; Wait 0.4; Key Escape; Typed 1 '"'; Key Escape
    Wait 1.2
    Typed 120 ' Backg'; Wait 0.8; Typed 120 'round'; Key Escape
    Typed 1 '='; Key Escape; Typed 1 '"'; Wait 1.2
    Typed 180 'Honeydew'; Wait 0.4; Key Escape; Typed 1 '"'; Key Escape
    Rest; Wait 2.2

    # A click in the preview selects the element's XAML.
    Click 1085 181; Wait 0.8; Rest
    Wait 2.5
    Click 760 300; Wait 0.8; Rest
    Wait 3
    Mark end
}
