using CaeManager.Application.ContratosAcceso.LecturaInbound;
using CaeManager.Application.ContratosAcceso.Queries.ObtenerLecturaInbound;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>La lectura Inbound entre Tenants solo la consumen los handlers que deciden antes la cartera.</b>
///
/// <para>
/// Las funciones <c>app_inbound_*</c> que hay detrás de <see cref="ILectorDocumentosInbound"/> garantizan el Tenant lector
/// (del contexto firmado), el contrato de acceso y la autorización del Tenant de la Empresa contratista, pero no la cartera:
/// no saben qué Clientes empresariales tiene asignados el Gestor CAE. Esa mitad la pone
/// <see cref="LecturaInboundAutoridad"/> en los handlers de <c>ObtenerLecturaInboundQueries</c>. Un consumidor nuevo que
/// inyectara el lector directamente leería los Documentos de otro Tenant de cualquier contrato del Tenant lector, fuera de
/// la cartera del usuario, y ningún test de comportamiento lo vería: la base respondería igual.
/// </para>
///
/// <para>
/// No congela el número de consumidores para siempre: obliga a que añadir uno pase por esta lista y por su motivo.
/// </para>
/// </summary>
public class ConsumidoresDelLectorInboundAcotadosTests
{
    private static readonly Type[] ConsumidoresAutorizados =
    [
        typeof(ObtenerAutorizacionInboundQueryHandler),
        typeof(ObtenerDocumentosInboundQueryHandler),
        typeof(ObtenerArchivoInboundQueryHandler),
    ];

    [Fact]
    public void Solo_los_handlers_de_ObtenerLecturaInboundQueries_dependen_del_lector_Inbound()
    {
        var consumidores = new[] { "CaeManager.Application", "CaeManager.Web" }
            .Select(ReflexionArquitecturaHelper.CargarAssembly)
            .SelectMany(a => ReflexionArquitecturaHelper.TiposQueReferencianTipo(a, typeof(ILectorDocumentosInbound)))
            .Select(Exterior)
            .Distinct()
            .ToList();

        consumidores.Should().BeEquivalentTo(ConsumidoresAutorizados,
            "las funciones app_inbound_* garantizan el Tenant, el contrato y la autorización, pero no la cartera; solo estos " +
            "handlers la deciden antes con LecturaInboundAutoridad, y otro consumidor leería Documentos de otro Tenant " +
            "fuera de la cartera del usuario");
    }

    [Fact]
    public void Los_handlers_autorizados_dependen_del_lector_y_de_la_cartera()
    {
        // Control: el escaneo de arriba ve a los tres; si dejaran de inyectar el lector, la lista se quedaría muerta.
        foreach (var handler in ConsumidoresAutorizados)
        {
            var parametros = handler.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();
            parametros.Should().Contain(typeof(ILectorDocumentosInbound), handler.Name);
            parametros.Should().Contain(typeof(CaeManager.Application.Common.IAlcanceDatosService),
                $"{handler.Name}: la cartera la decide quien llama al lector");
        }
    }

    // Los tipos que genera el compilador (máquinas de estado, clausuras) se cuentan como su tipo exterior.
    private static Type Exterior(Type tipo)
    {
        while (tipo.DeclaringType is not null)
            tipo = tipo.DeclaringType;
        return tipo;
    }
}
