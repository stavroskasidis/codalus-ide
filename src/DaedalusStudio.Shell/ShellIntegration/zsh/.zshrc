# Daedalus Studio shell integration for zsh (ADR 0014): load the user's .zshrc from their own ZDOTDIR, then report the
# working directory (OSC 7) and command marks (OSC 133).
ZDOTDIR="${DAEDALUS_USER_ZDOTDIR:-$HOME}"
unset DAEDALUS_USER_ZDOTDIR
if [[ -r "$ZDOTDIR/.zshrc" ]]; then
  . "$ZDOTDIR/.zshrc"
fi

__daedalus_in_command=

__daedalus_precmd() {
  local exit_status=$?
  if [[ -n "$__daedalus_in_command" ]]; then
    printf '\e]133;D;%s\a' "$exit_status"
  fi
  __daedalus_in_command=
  printf '\e]7;file://%s%s\a' "${HOST:-localhost}" "${PWD// /%20}"
  printf '\e]133;A\a'
}

__daedalus_preexec() {
  __daedalus_in_command=1
  printf '\e]133;C\a'
}

autoload -Uz add-zsh-hook
add-zsh-hook precmd __daedalus_precmd
add-zsh-hook preexec __daedalus_preexec
PS1="${PS1}%{$(printf '\e]133;B\a')%}"
