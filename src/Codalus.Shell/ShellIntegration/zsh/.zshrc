# Codalus IDE shell integration for zsh (ADR 0014): load the user's .zshrc from their own ZDOTDIR, then report the
# working directory (OSC 7) and command marks (OSC 133).
ZDOTDIR="${CODALUS_USER_ZDOTDIR:-$HOME}"
unset CODALUS_USER_ZDOTDIR
if [[ -r "$ZDOTDIR/.zshrc" ]]; then
  . "$ZDOTDIR/.zshrc"
fi

__codalus_in_command=

__codalus_precmd() {
  local exit_status=$?
  if [[ -n "$__codalus_in_command" ]]; then
    printf '\e]133;D;%s\a' "$exit_status"
  fi
  __codalus_in_command=
  printf '\e]7;file://%s%s\a' "${HOST:-localhost}" "${PWD// /%20}"
  printf '\e]133;A\a'
}

__codalus_preexec() {
  __codalus_in_command=1
  printf '\e]133;C\a'
}

autoload -Uz add-zsh-hook
add-zsh-hook precmd __codalus_precmd
add-zsh-hook preexec __codalus_preexec
PS1="${PS1}%{$(printf '\e]133;B\a')%}"
