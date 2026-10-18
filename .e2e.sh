export MSBUILDDISABLENODEREUSE=1
dotnet build tests/CaeManager.E2ETests -warnaserror -nologo -v q -nodeReuse:false 2>&1 | grep -E "error|Errores" | head -3 | cut -c1-250
bash scripts/turno-postgres.sh --espera-max-min 30 -- dotnet test tests/CaeManager.E2ETests --no-build --nologo --filter "FullyQualifiedName~CentrosGestionarEnVivo|FullyQualifiedName~DeepLinks|FullyQualifiedName~GestorCaeCarteraMultiTenant|FullyQualifiedName~RecorridoFichas360|FullyQualifiedName~VisitaCentroGestionadoPorCorreo|FullyQualifiedName~CoordinadorCaeTenantBeneficiario|FullyQualifiedName~AltaGuiada|FullyQualifiedName~ImportacionTests|FullyQualifiedName~ImportarCombinado|FullyQualifiedName~FlujoCicloDocumental" > .e2e.log 2>&1
echo exit $?
tail -3 .e2e.log | cut -c1-250
grep -E "^\s+Con error " .e2e.log | head
