# Daedalus Studio shell integration for bash (ADR 0014). The terminal starts bash with --init-file pointing here:
# load the user's own startup files, then report the working directory (OSC 7) and command marks (OSC 133).
if [ "${DAEDALUS_BASH_LOGIN:-}" = 1 ]; then
  [ -r /etc/profile ] && . /etc/profile
  for __daedalus_file in ~/.bash_profile ~/.bash_login ~/.profile; do
    if [ -r "$__daedalus_file" ]; then
      . "$__daedalus_file"
      break
    fi
  done
  unset __daedalus_file
else
  [ -r /etc/bash.bashrc ] && . /etc/bash.bashrc
  [ -r ~/.bashrc ] && . ~/.bashrc
fi
unset DAEDALUS_BASH_LOGIN

__daedalus_in_command=
__daedalus_prompt_shown=

__daedalus_prompt() {
  local status=$?
  if [ -n "$__daedalus_in_command" ]; then
    printf '\e]133;D;%s\a' "$status"
  fi
  __daedalus_in_command=
  printf '\e]7;file://%s%s\a' "${HOSTNAME:-localhost}" "${PWD// /%20}"
  printf '\e]133;A\a'
  __daedalus_prompt_shown=1
  return $status
}

__daedalus_preexec() {
  # The DEBUG trap runs before every simple command; only the first one after a prompt starts a command.
  [ -n "${COMP_LINE:-}" ] && return
  [ "$__daedalus_prompt_shown" = 1 ] || return
  __daedalus_prompt_shown=
  __daedalus_in_command=1
  printf '\e]133;C\a'
}

if [[ "${PROMPT_COMMAND:-}" != *__daedalus_prompt* ]]; then
  PROMPT_COMMAND="${PROMPT_COMMAND:+$PROMPT_COMMAND; }__daedalus_prompt"
  PS1="${PS1}\[\e]133;B\a\]"
  trap '__daedalus_preexec' DEBUG
fi
