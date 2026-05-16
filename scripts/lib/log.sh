#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# lib/log.sh — Coloured logging helpers
# Source this file; do not execute it directly.
# ─────────────────────────────────────────────────────────────────────────────

# Only use colours when stdout is a real terminal
if [[ -t 1 ]]; then
  _CLR_RESET='\033[0m'
  _CLR_BOLD='\033[1m'
  _CLR_CYAN='\033[0;36m'
  _CLR_GREEN='\033[0;32m'
  _CLR_YELLOW='\033[0;33m'
  _CLR_RED='\033[0;31m'
  _CLR_BLUE='\033[0;34m'
else
  _CLR_RESET='' _CLR_BOLD='' _CLR_CYAN='' _CLR_GREEN=''
  _CLR_YELLOW='' _CLR_RED='' _CLR_BLUE=''
fi

log_info()    { echo -e "${_CLR_CYAN}${_CLR_BOLD}[INFO]${_CLR_RESET}  $*"; }
log_ok()      { echo -e "${_CLR_GREEN}${_CLR_BOLD}[ OK ]${_CLR_RESET}  $*"; }
log_warn()    { echo -e "${_CLR_YELLOW}${_CLR_BOLD}[WARN]${_CLR_RESET}  $*"; }
log_error()   { echo -e "${_CLR_RED}${_CLR_BOLD}[ERR ]${_CLR_RESET}  $*" >&2; }
log_section() { echo -e "\n${_CLR_BLUE}${_CLR_BOLD}══ $* ══${_CLR_RESET}"; }
log_step()    { echo -e "${_CLR_BOLD}  ▶ $*${_CLR_RESET}"; }

