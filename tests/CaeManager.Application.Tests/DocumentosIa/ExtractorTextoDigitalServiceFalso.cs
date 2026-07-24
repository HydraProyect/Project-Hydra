using CaeManager.Application.Common;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Tests.DocumentosIa;

public class ExtractorTextoDigitalServiceFalso(Result<IReadOnlyList<string>> resultado) : IExtractorTextoDigitalService
{
    public Result<IReadOnlyList<string>> ExtraerTextoPorPagina(byte[] contenidoPdf) => resultado;
}
