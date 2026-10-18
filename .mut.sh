export MSBUILDDISABLENODEREUSE=1
sed -i 's|<MenuAcciones EtiquetaAccesible="@Textos\["MenuFilaEtiqueta", centro.Nombre\]">|<MenuAcciones>|' src/CaeManager.Web/Features/Centros/Pages/Centros.razor
git diff --stat | tail -1
dotnet build tests/CaeManager.Web.Tests -nologo -v q -nodeReuse:false > .mut-build.log 2>&1 || echo BUILD FALLA
dotnet test tests/CaeManager.Web.Tests --no-build --nologo --filter "FullyQualifiedName~CentrosListaPatronTests" 2>&1 | grep -E "^\s+Con error |Con error!|Correctas!" | cut -c1-230
git restore src
git status -s
