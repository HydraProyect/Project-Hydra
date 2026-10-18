export MSBUILDDISABLENODEREUSE=1
for p in CaeManager.Web.Tests CaeManager.Architecture.Tests CaeManager.Application.Tests; do
  dotnet build tests/$p -warnaserror -nologo -v q -nodeReuse:false 2>&1 | grep -E "Errores|error" | head -2 | cut -c1-200
  dotnet test tests/$p --no-build --nologo 2>&1 | tail -1
done
