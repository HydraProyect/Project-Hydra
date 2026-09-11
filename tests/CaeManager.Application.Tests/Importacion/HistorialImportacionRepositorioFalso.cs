using CaeManager.Domain.Importacion;

namespace CaeManager.Application.Tests.Importacion;

internal sealed class HistorialImportacionRepositorioFalso : IHistorialImportacionRepository
{
    public List<HistorialImportacion> Registros { get; } = [];

    public void Agregar(HistorialImportacion registro) => Registros.Add(registro);
}
