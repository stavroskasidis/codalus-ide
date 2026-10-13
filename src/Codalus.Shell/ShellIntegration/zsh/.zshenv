# Codalus IDE shell integration for zsh (ADR 0014): the terminal points ZDOTDIR here; load the user's .zshenv.
if [[ -r "${CODALUS_USER_ZDOTDIR:-$HOME}/.zshenv" ]]; then
  . "${CODALUS_USER_ZDOTDIR:-$HOME}/.zshenv"
fi
