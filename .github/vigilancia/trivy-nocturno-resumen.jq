# Resumen en Markdown del informe JSON de Trivy del escaneo nocturno sin filtro
# (.github/workflows/trivy-nocturno.yml): recuento por severidad, con y sin
# corrección, y la lista de CRITICAL y HIGH. Prueba en trivy-nocturno-resumen.test.js.
#
# Uso: jq -r -f .github/vigilancia/trivy-nocturno-resumen.jq informe.json

# Texto de terceros dentro de una tabla Markdown: sin barras, saltos de línea, HTML,
# menciones, corchetes ni comillas invertidas.
def celda: tostring | gsub("[|<>@\\[\\]`\r\n\t]"; " ");
def corregida: (.FixedVersion // "") != "";

[.Results[]?.Vulnerabilities[]?] as $v
| (["CRITICAL", "HIGH", "MEDIUM", "LOW", "UNKNOWN"]
   | map(. as $s | "| \($s) | \($v | map(select(.Severity == $s and corregida)) | length) | \($v | map(select(.Severity == $s and (corregida | not))) | length) |")) as $filas
| ($v
   | map(select(.Severity == "CRITICAL" or .Severity == "HIGH"))
   | unique_by([.VulnerabilityID, .PkgName, .InstalledVersion])
   | sort_by([(if .Severity == "CRITICAL" then 0 else 1 end), .VulnerabilityID])
   | map("| " + ([.VulnerabilityID, .Severity, .PkgName, .InstalledVersion,
                  (if corregida then .FixedVersion else "sin corrección" end)]
                 | map(celda) | join(" | ")) + " |")) as $graves
| (["| Severidad | Con corrección | Sin corrección |", "| --- | ---: | ---: |"] + $filas
   + ["", "### CRITICAL y HIGH (las sin corrección no bloquean los gates de PR ni de despliegue)", ""]
   + (if ($graves | length) == 0 then ["Ninguna."]
      else ["| CVE | Severidad | Paquete | Instalada | Corregida en |", "| --- | --- | --- | --- | --- |"] + $graves end))
| join("\n")
