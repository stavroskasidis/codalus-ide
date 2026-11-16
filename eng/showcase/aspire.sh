# shellcheck shell=bash disable=SC2034,SC2154 # a story: record.sh sources it, sets root and WORK, and reads OPEN, SETTINGS, LOAD
# The "Aspire and containers" tab of the home page's app models (website/screenshots.txt): an Aspire AppHost running its
# projects, and the Aspire window with their states and each one's console output. Needs docker. (Not the Environment
# tab: it shows the variables the recording machine gives the projects.)
# eng/showcase/record.sh plays it.

prepare() {
  local app="$WORK/src/Shop"
  cp -r "$root/samples/Containers" "$app"
  # The sample's AppHost without its notes for the tests.
  cat > "$app/Aspire/AppHost/AppHost.cs" << 'EOF'
var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache").WithExplicitStart();

var api = builder.AddProject<Projects.Api>("api").WithHttpEndpoint();

builder.AddProject<Projects.Frontend>("frontend")
    .WithHttpEndpoint()
    .WithReference(api)
    .WaitFor(api);

builder.Build().Run();
EOF
  OPEN=("$app/Aspire.slnx" --open "$app/Aspire/AppHost/AppHost.cs")
  LOAD=60
}

setup() {
  click 484 46; sleep 0.8; click 478 100; sleep 1    # the AppHost starts
  xdotool key ctrl+shift+b; sleep 60
  click 973 589; sleep 0.5; click 973 589            # the Error List and the build's Output close
}

story() {
  sleep 0.5
  mark start
  sleep 1
  xdotool key ctrl+F5; rest; sleep 2.5            # Ctrl+F5: the AppHost runs its projects
  click 192 85; rest; sleep 0.5; click 973 589; rest; sleep 3.5
  # Each resource's console output, row by row (the rows' order changes from run to run).
  click 120 141; rest; sleep 3
  click 120 177; rest; sleep 3
  click 120 210; rest; sleep 2
  mark end
}
