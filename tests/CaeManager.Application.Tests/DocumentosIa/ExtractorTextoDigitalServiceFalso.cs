using CaeManager.Application.Common;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Tests.DocumentosIa;

public class ExtractorTextoDigitalServiceFalso(Result<string> resultado) : IExtractorTextoDigitalService
{
    public Result<string> ExtraerTexto(byte[] contenidoPdf) => resultado;
}
