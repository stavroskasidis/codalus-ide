# Codalus IDE shell integration for bash (ADR 0014). The terminal starts bash with --init-file pointing here:
# load the user's own startup files, then report the working directory (OSC 7) and command marks (OSC 133).
if [ "${CODALUS_BASH_LOGIN:-}" = 1 ]; then
  [ -r /etc/profile ] && . /etc/profile
  for __codalus_file in ~/.bash_profile ~/.bash_login ~/.profile; do
    if [ -r "$__codalus_file" ]; then
      . "$__codalus_file"
      break
    fi
  done
  unset __codalus_file
else
  [ -r /etc/bash.bashrc ] && . /etc/bash.bashrc
  [ -r ~/.bashrc ] && . ~/.bashrc
fi
unset CODALUS_BASH_LOGIN

__codalus_in_command=
__codalus_prompt_shown=

__codalus_prompt() {
  local status=$?
  if [ -n "$__codalus_in_command" ]; then
    printf '\e]133;D;%s\a' "$status"
  fi
  __codalus_in_command=
  printf '\e]7;file://%s%s\a' "${HOSTNAME:-localhost}" "${PWD// /%20}"
  printf '\e]133;A\a'
  __codalus_prompt_shown=1
  return $status
}

__codalus_preexec() {
  # The DEBUG trap runs before every simple command; only the first one after a prompt starts a command.
  [ -n "${COMP_LINE:-}" ] && return
  [ "$__codalus_prompt_shown" = 1 ] || return
  __codalus_prompt_shown=
  __codalus_in_command=1
  printf '\e]133;C\a'
}

if [[ "${PROMPT_COMMAND:-}" != *__codalus_prompt* ]]; then
  PROMPT_COMMAND="${PROMPT_COMMAND:+$PROMPT_COMMAND; }__codalus_prompt"
  PS1="${PS1}\[\e]133;B\a\]"
  trap '__codalus_preexec' DEBUG
fi
