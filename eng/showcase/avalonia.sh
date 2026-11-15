# shellcheck shell=bash disable=SC2034,SC2154 # a story: record.sh sources it, sets root and WORK, and reads OPEN, SETTINGS, LOAD
# The Avalonia tab of the home page's app models (website/screenshots.txt): edits to an .axaml file that the XAML
# Previewer beside it draws as they're typed, then a click in the preview that selects its XAML.
# eng/showcase/record.sh plays it.

prepare() {
  local app="$WORK/src/TodoApp"
  cp -r "$root/samples/Avalonia" "$app"
  sed -i 's/Greeting = "Hello from design-time data"/Greeting = "Today"/' "$app/AvaloniaApp/ViewModels/DesignData.cs"
  # The sample's window, with lines short enough for the editor beside a wide preview.
  cat > "$app/AvaloniaApp/Views/MainWindow.axaml" << 'EOF'
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
        xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
        xmlns:vm="using:AvaloniaApp.ViewModels"
        xmlns:views="using:AvaloniaApp.Views"
        xmlns:controls="using:AvaloniaControls"
        mc:Ignorable="d" d:DesignWidth="480" d:DesignHeight="360"
        d:DataContext="{x:Static vm:DesignData.Main}"
        x:Class="AvaloniaApp.Views.MainWindow"
        x:DataType="vm:MainWindowViewModel"
        Title="To-do" Width="480" Height="360">

  <DockPanel Margin="12">
    <TextBlock DockPanel.Dock="Top" Classes="title"
               Text="{Binding Greeting}" />

    <StackPanel DockPanel.Dock="Bottom"
                Orientation="Horizontal"
                Spacing="{StaticResource CardSpacing}">
      <Button Content="Increment"
              Command="{Binding Increment}" />
      <TextBlock VerticalAlignment="Center"
                 Text="{Binding Count}" />
      <controls:RatingControl Value="3" Maximum="5" />
    </StackPanel>

    <ListBox ItemsSource="{Binding Items}">
      <ListBox.ItemTemplate>
        <DataTemplate x:DataType="vm:TodoItemViewModel">
          <views:TodoItemView />
        </DataTemplate>
      </ListBox.ItemTemplate>
    </ListBox>
  </DockPanel>
</Window>
EOF
  printf 'using Avalonia.Controls;\n\nnamespace AvaloniaApp.Views;\n\npublic partial class MainWindow : Window\n{\n    public MainWindow() => InitializeComponent();\n}\n' \
    > "$app/AvaloniaApp/Views/MainWindow.axaml.cs"
  OPEN=("$app/Avalonia.slnx" --open "$app/AvaloniaApp/Views/MainWindow.axaml")
  LOAD=60
}

setup() {
  click 973 589; sleep 0.5; click 973 589       # the Error List and Output close: the editor gets the height
  xdotool key shift+F7                            # the XAML Previewer, which builds the app first
  sleep 60
  xdotool mousemove --sync 993 300; xdotool mousedown 1; sleep 0.2
  xdotool mousemove --sync 900 300; sleep 0.1; xdotool mousemove --sync 700 300; sleep 0.1
  xdotool mousemove --sync 740 300; sleep 0.2; xdotool mouseup 1; sleep 1.5
  xdotool mousemove --sync 300 400; xdotool click --repeat 4 --delay 80 5; sleep 0.5
}

# The editor scrolled to line 14 (y 166), 17.5 pixels a line.
line() { echo $((166 + ($1 - 14) * 35 / 2)); }

story() {
  sleep 0.5
  mark start
  sleep 1
  # A bigger, colored title: attributes typed with completion, each on a line of its own.
  click 600 "$(line 16)"; xdotool key End Left Left; sleep 0.4; xdotool key Return; sleep 0.5
  typed 130 "Fo"; sleep 1.2; typed 120 "ntSize"; sleep 0.3; xdotool key Escape
  typed 1 "="; sleep 0.1; xdotool key Escape; typed 1 '"'; sleep 1; typed 160 '30'; xdotool key Escape; typed 1 '"'; sleep 0.1; xdotool key Escape
  sleep 1.4
  xdotool key Return; typed 120 "Foreground"; sleep 0.3; xdotool key Escape
  typed 1 "="; sleep 0.1; xdotool key Escape; typed 1 '"'; sleep 1.2
  typed 200 "Teal"; sleep 0.8; xdotool key Escape; typed 1 '"'; sleep 0.1; xdotool key Escape

  # The button's text and the rating (two lines down now).
  click 264 "$(line 23)"; xdotool key ctrl+shift+Right; sleep 0.6
  typed 110 "Add a task"; sleep 0.3; xdotool key Escape
  rest; sleep 1.8
  click 373 "$(line 27)"; xdotool key shift+Right; sleep 0.5
  typed 110 "5"; sleep 0.3; xdotool key Escape
  rest; sleep 2

  # A click in the preview selects the element's XAML.
  click 815 489; sleep 0.8; rest
  sleep 3
  mark end
}
