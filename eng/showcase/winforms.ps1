# A story (eng/showcase/record.ps1 plays it): the "Windows Forms" tab of the home page's app models. A button dropped
# from the Toolbox onto a form in the Windows Forms designer, moved and sized along the snap lines, and its Text set in
# the Properties window.

# On the designer's empty space: the Properties window's values are text, under which the pointer is an I-beam.
$RestAt = 870, 400

function Prepare {
    $app = "$Work\src\WinFormsApp"
    New-Item -ItemType Directory -Force $app | Out-Null
    $desktop = Join-Path $Root 'samples/WindowsDesktop'
    Copy-Item -Recurse "$desktop/WinFormsApp" "$app/WinFormsApp"
    Copy-Item "$desktop/WinFormsApp.slnx", "$desktop/Directory.Build.props" $app
    $script:Open = @("$app\WinFormsApp.slnx", '--open', "$app\WinFormsApp\OrderForm.cs")
    $script:Load = 90                                # the designer builds the app first
}

function Setup {
    Click 973 589; Wait 0.5; Click 973 589          # the Error List and Output close
    Key ctrl+alt+x; Wait 1.5                         # the Toolbox, on the left
    Click 500 520; Key F4; Wait 1.5                  # the form selected, and the Properties window on the right
}

function Story {
    Wait 0.5
    Mark start
    Wait 1
    # A Button from the Toolbox, dropped below the counter.
    Drag 58 275 352 512; Wait 0.8
    Click 389 523; Wait 0.6
    # Moved left until it lines up with the list (a snap line), then its Text set in the Properties window.
    Drag 389 523 365 523
    Rest; Wait 1
    Click 1210 595; Wait 0.4; Key ctrl+a
    Typed 110 'Print receipt'; Wait 0.5; Key Return
    Rest; Wait 1.5
    # Its right edge dragged out to the list's.
    Drag 403 525 446 525
    Rest; Wait 3
    Mark end
}
