# Daedalus Studio shell integration for zsh (ADR 0014): the terminal points ZDOTDIR here; load the user's .zshenv.
if [[ -r "${DAEDALUS_USER_ZDOTDIR:-$HOME}/.zshenv" ]]; then
  . "${DAEDALUS_USER_ZDOTDIR:-$HOME}/.zshenv"
fi
