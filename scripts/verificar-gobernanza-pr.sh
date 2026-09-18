#!/bin/bash
# SABOTAJE DELIBERADO — prueba de mutación de REC-213 (turno 2026-09-18, T4).
# Esta versión sale 0 siempre, sin comprobar nada, simulando una PR que
# intenta desactivar la gobernanza editando el propio guion. Si el job
# "Gobernanza — metadatos de PR" sale en VERDE con esta PR pese a que el
# cuerpo omite a propósito la sección "## Revisión Codex", REC-213 NO está
# cerrado: el checkout de base.sha (o el disparador pull_request_target) no
# estaría usando la versión de confianza. Si sale en ROJO, confirma que el
# guion que corre de verdad es el de origin/main, no este.
exit 0
