using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;

namespace CaeManager.Web.Features.Proyectos.Pages;

public partial class Proyectos
{
    private string _agrupar = string.Empty;
    private readonly HashSet<Guid> _expandidos = [];

    /// <summary>Los grupos abiertos de la vista: todos nacen plegados y se recuerdan hasta recargar (como Vehículos).</summary>
    private readonly GruposAbiertos _gruposAbiertos = new();

    private bool Agrupado => _agrupar == "empresa";

    /// <summary>Filas que se piden por página: agrupada, todas las filtradas, para que el tamaño de página no corte los grupos.</summary>
    private int TamanoPedido => AgrupacionDeLista.TamanoPedido(Agrupado, _tamanoPagina);

    /// <summary>Las filas con la cabecera del grupo «Por Cliente» en la primera de cada uno; los grupos plegados las ocultan.</summary>
    private IEnumerable<FilaDeGrupo<ProyectoListaDto>> FilasConGrupo() =>
        GruposDeLista.Agrupar(_proyectos, Agrupado, p => p.ClienteId.ToString(), (_, p) => p.ClienteRazonSocial, EsFilaRoja, _gruposAbiertos);

    /// <summary>Fila en rojo: lo que falta o vence (el mismo criterio que Vehículos para la pastilla «N con incidencias» del grupo).</summary>
    private static bool EsFilaRoja(ProyectoListaDto proyecto) => proyecto.EstadoDocumental is EstadoDocumento.Faltante or EstadoDocumento.Vencido;

    /// <summary>
    /// Las filas que se ven: sin agrupar, las de la página; agrupada, solo las de los grupos abiertos. El foco, los atajos
    /// y «Expandir todo» operan sobre estas, nunca sobre filas ocultas por un grupo plegado.
    /// </summary>
    private List<ProyectoListaDto> FilasPintadas =>
        Agrupado ? _proyectos.Where(p => _gruposAbiertos.EstaAbierto(p.ClienteId.ToString())).ToList() : _proyectos;

    /// <summary>Abre o pliega un grupo; al plegarlo, sus filas dejan de estar enfocadas y expandidas.</summary>
    private void AlternarGrupo(string clave)
    {
        _gruposAbiertos.Alternar(clave);
        var pintadas = FilasPintadas;
        if (_idEnfocado is { } enfocado && pintadas.All(p => p.Id != enfocado)) _idEnfocado = null;
    }

    private Task CambiarAgrupacion(string valor)
    {
        _agrupar = valor == "empresa" ? valor : string.Empty;
        // Al agrupar todo nace plegado: el foco previo apuntaría a una fila que ya no se ve.
        _idEnfocado = null;
        return RecargarDesdeLaPrimeraPaginaAsync();
    }

    private bool TodosExpandidos => FilasPintadas is { Count: > 0 } pintadas && pintadas.All(p => _expandidos.Contains(p.Id));

    private void AlternarTodosExpandidos(bool expandir)
    {
        if (!expandir)
        {
            _expandidos.Clear();
            return;
        }

        foreach (var proyecto in FilasPintadas) _expandidos.Add(proyecto.Id);
    }

    private void AlternarDocumentacion(Guid id)
    {
        if (!_expandidos.Add(id)) _expandidos.Remove(id);
    }
}