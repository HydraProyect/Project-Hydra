using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.TiposDocumento;

/// <summary>
/// La búsqueda por texto es la razón de ser del campo de alias (taxonomía
/// documental CAE §2bis): antes de que existiera, "TC2" solo se encontraba si
/// estaba escrito dentro del Nombre.
/// </summary>
public class ObtenerTiposDocumentoQueryBusquedaTests
{
    private static (TiposDocumentoQueryContextFalso Contexto, CentrosQueryContextFalso Centros) ContextoCon(TipoDocumento tipo)
    {
        var contexto = new TiposDocumentoQueryContextFalso();
        contexto.ListaTiposDocumento.Add(tipo);
        foreach (var alias in tipo.Aliases)
            contexto.ListaTiposDocumentoAlias.Add(alias);

        return (contexto, new CentrosQueryContextFalso());
    }

    [Fact]
    public async Task Encuentra_por_alias_aunque_no_este_en_el_nombre()
    {
        var tipo = new TipoDocumento("Relación Nominal de Trabajadores", null, false, 1, AmbitoAplicacion.Empresa);
        tipo.EstablecerAliases(["TC2"]);
        var (contexto, centros) = ContextoCon(tipo);
        var handler = new ObtenerTiposDocumentoQueryHandler(centros, contexto);

        var resultado = await handler.Handle(new ObtenerTiposDocumentoQuery(Texto: "tc2"), CancellationToken.None);

        resultado.Should().ContainSingle(t => t.Id == tipo.Id);
    }

    [Fact]
    public async Task Encuentra_por_nombre()
    {
        var tipo = new TipoDocumento("Relación Nominal de Trabajadores", null, false, 1, AmbitoAplicacion.Empresa);
        var (contexto, centros) = ContextoCon(tipo);
        var handler = new ObtenerTiposDocumentoQueryHandler(centros, contexto);

        var resultado = await handler.Handle(new ObtenerTiposDocumentoQuery(Texto: "nominal"), CancellationToken.None);

        resultado.Should().ContainSingle(t => t.Id == tipo.Id);
    }

    [Fact]
    public async Task No_encuentra_texto_ajeno_al_nombre_y_a_los_alias()
    {
        var tipo = new TipoDocumento("Relación Nominal de Trabajadores", null, false, 1, AmbitoAplicacion.Empresa);
        tipo.EstablecerAliases(["TC2"]);
        var (contexto, centros) = ContextoCon(tipo);
        var handler = new ObtenerTiposDocumentoQueryHandler(centros, contexto);

        var resultado = await handler.Handle(new ObtenerTiposDocumentoQuery(Texto: "seguro"), CancellationToken.None);

        resultado.Should().BeEmpty();
    }
}
