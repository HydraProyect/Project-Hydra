using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;

namespace CaeManager.Web.Features.Proyectos.Pages;

public partial class Proyectos
{
    private string _agrupar = string.Empty;
    private readonly HashSet<Guid> _expandidos = [];

    private sealed record GrupoEmpresa(Guid Id, string Nombre);

    private IEnumerable<IGrouping<GrupoEmpresa, ProyectoListaDto>> BloquesDeProyectos =>
        _proyectos.GroupBy(p => _agrupar == "empresa"
            ? new GrupoEmpresa(p.ClienteId, p.ClienteRazonSocial)
            : new GrupoEmpresa(Guid.Empty, string.Empty));

    private Task CambiarAgrupacion(string valor)
    {
        _agrupar = valor == "empresa" ? valor : string.Empty;
        return RecargarDesdeLaPrimeraPaginaAsync();
    }

    private bool TodosExpandidos => _proyectos.Count > 0 && _proyectos.All(p => _expandidos.Contains(p.Id));

    private void AlternarTodosExpandidos(bool expandir)
    {
        if (!expandir)
        {
            _expandidos.Clear();
            return;
        }

        foreach (var proyecto in _proyectos) _expandidos.Add(proyecto.Id);
    }

    private void AlternarDocumentacion(Guid id)
    {
        if (!_expandidos.Add(id)) _expandidos.Remove(id);
    }
}
