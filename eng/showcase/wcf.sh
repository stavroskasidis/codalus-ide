# shellcheck shell=bash disable=SC2034,SC2154 # a story: record.sh sources it, sets root and WORK, and reads OPEN, SETTINGS, LOAD
# The WCF tab of the home page's app models (website/screenshots.txt): a CoreWCF service running, and the WCF Test
# Client calling its operations from typed forms, the response as a tree. eng/showcase/record.sh plays it.

prepare() {
  local app="$WORK/src/Calculator"
  cp -r "$root/samples/Wcf" "$app"
  OPEN=("$app/Wcf.slnx" --open "$app/CalculatorService/ICalculatorService.cs")
  LOAD=45
}

setup() {
  click 484 46; sleep 0.8; click 501 100; sleep 1    # CalculatorService starts
  xdotool key ctrl+F5; sleep 45
  click 973 589; sleep 0.5; click 973 589            # the Error List and the service's Output close
  xdotool key ctrl+q; sleep 0.8; typed 30 "WCF Test Client"; sleep 1.5; xdotool key Return; sleep 4
}

story() {
  sleep 0.5
  mark start
  sleep 1
  click 400 181; sleep 0.5; typed 40 "http://localhost:5280/CalculatorService.svc?wsdl"; xdotool key Return; sleep 3
  click 710 319; typed 120 "2"; click 710 351; typed 120 "3.5"; sleep 0.4; xdotool key F5; rest; sleep 2.5
  click 77 422; rest; sleep 1.5
  click 710 351; typed 120 "Ada"; click 710 383; typed 120 "36"; click 710 412; sleep 0.8; xdotool key Down Down Return; sleep 0.6
  click 887 447; sleep 0.6; click 710 479; typed 120 "math"; sleep 0.4
  xdotool key F5; rest; sleep 3
  click 895 603; rest; sleep 3                   # the response's SOAP envelope
  mark end
}
