using CaeManager.Application.ContratosAcceso.EvidenciasInbound;
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
/// <see cref="LecturaInboundAutoridad"/> en los handlers de <c>ObtenerLecturaInboundQueries</c> y en
/// <c>RegistrarEvidenciaInboundCommandHandler</c>. Un consumidor nuevo que
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
        // Inbound M1, PR 4: registrar la copia de evidencia validada lee la autorización, el Documento y su archivo. Decide
        // antes la autoridad con LecturaInboundAutoridad, como los tres de arriba.
        typeof(RegistrarEvidenciaInboundCommandHandler),
    ];

    /// <summary>
    /// <see cref="IAlmacenOriginalesInbound"/> abre binarios de la carpeta del Tenant de la Empresa contratista con su clave:
    /// cruza a propósito el aislamiento del almacenamiento por Tenant, así que solo lo usan quienes ya pasaron la autoridad
    /// y obtuvieron la referencia de <c>app_inbound_archivo_autorizado</c>. <see cref="OriginalesInbound"/> es el auxiliar
    /// que lo lee entero y no decide nada.
    /// </summary>
    private static readonly Type[] ConsumidoresAutorizadosDelAlmacenDeOriginales =
    [
        typeof(ObtenerArchivoInboundQueryHandler),
        typeof(RegistrarEvidenciaInboundCommandHandler),
        typeof(OriginalesInbound),
    ];

    [Fact]
    public void Solo_los_handlers_que_deciden_la_cartera_dependen_del_lector_Inbound()
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
        // Control: el escaneo de arriba ve a los cuatro; si dejaran de inyectar el lector, la lista se quedaría muerta.
        foreach (var handler in ConsumidoresAutorizados)
        {
            var parametros = handler.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();
            parametros.Should().Contain(typeof(ILectorDocumentosInbound), handler.Name);
            parametros.Should().Contain(typeof(CaeManager.Application.Common.IAlcanceDatosService),
                $"{handler.Name}: la cartera la decide quien llama al lector");
        }
    }

    [Fact]
    public void Solo_los_consumidores_del_lector_abren_originales_del_Tenant_de_la_Empresa_contratista()
    {
        var consumidores = new[] { "CaeManager.Application", "CaeManager.Web" }
            .Select(ReflexionArquitecturaHelper.CargarAssembly)
            .SelectMany(a => ReflexionArquitecturaHelper.TiposQueReferencianTipo(a, typeof(IAlmacenOriginalesInbound)))
            .Select(Exterior)
            .Where(t => t != typeof(IAlmacenOriginalesInbound))
            .Distinct()
            .ToList();

        consumidores.Should().BeEquivalentTo(ConsumidoresAutorizadosDelAlmacenDeOriginales,
            "abrir un original con la clave de otro Tenant solo es legítimo después de la autoridad de la lectura y con la " +
            "referencia que devuelve la base; otro consumidor podría leer binarios de cualquier Tenant");
        ConsumidoresAutorizadosDelAlmacenDeOriginales.Where(t => t != typeof(OriginalesInbound))
            .Should().OnlyContain(t => ConsumidoresAutorizados.Contains(t), "quien abre el original es quien decide antes la autoridad");
    }

    // Los tipos que genera el compilador (máquinas de estado, clausuras) se cuentan como su tipo exterior.
    private static Type Exterior(Type tipo)
    {
        while (tipo.DeclaringType is not null)
            tipo = tipo.DeclaringType;
        return tipo;
    }
}
