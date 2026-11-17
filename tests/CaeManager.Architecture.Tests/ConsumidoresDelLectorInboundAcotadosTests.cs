using CaeManager.Application.Inbound.ContratosAcceso.EvidenciasInbound;
using CaeManager.Application.Inbound.ContratosAcceso.LecturaInbound;
using CaeManager.Application.Inbound.ContratosAcceso.Queries.ObtenerColaValidacionInbound;
using CaeManager.Application.Inbound.ContratosAcceso.Queries.ObtenerLecturaInbound;
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
/// <see cref="LecturaInboundAutoridad"/> en los handlers de <c>ObtenerLecturaInboundQueries</c>, en
/// <c>ObtenerColaValidacionInboundQueryHandler</c> y en <c>RegistrarEvidenciaInboundCommandHandler</c>. Un consumidor nuevo que
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
        // Demo mínima (DECISION_LOG 2026-11-17, opción b): la cola de validación lista los Documentos autorizados de cada
        // contrato después de LecturaInboundAutoridad sobre ese contrato, igual que ObtenerDocumentosInboundQueryHandler.
        typeof(ObtenerColaValidacionInboundQueryHandler),
        // Demo Inbound P6: el archivo pedido desde la cola de validación. Pide ValidarInbound y LeerInbound y luego abre con
        // la misma apertura que ObtenerArchivoInboundQueryHandler, que decide la cartera con LecturaInboundAutoridad.
        typeof(ObtenerArchivoParaValidarInboundQueryHandler),
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
        typeof(ObtenerArchivoParaValidarInboundQueryHandler),
        typeof(RegistrarEvidenciaInboundCommandHandler),
        typeof(OriginalesInbound),
    ];

    // Infrastructure también: un servicio de fondo o un adaptador que inyectara el lector o el almacén de originales leería
    // otro Tenant sin la cartera, igual que uno de Application o Web. Sus implementaciones no cuentan como consumidoras
    // (implementar no es inyectar) y el registro de dependencias solo resuelve, no inyecta.
    private static readonly string[] EnsambladosEscaneados = ["CaeManager.Application", "CaeManager.Web", "CaeManager.Infrastructure"];

    [Fact]
    public void Solo_los_handlers_que_deciden_la_cartera_dependen_del_lector_Inbound()
    {
        var consumidores = EnsambladosEscaneados
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
        var consumidores = EnsambladosEscaneados
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

    /// <summary>
    /// Los dos escaneos de arriba miran constructores, campos y propiedades: no ven un
    /// <c>GetRequiredService&lt;IAlmacenOriginalesInbound&gt;()</c>, un <c>typeof</c> ni un cast dentro del cuerpo de un
    /// método. Este lee también el IL de los cuerpos (<see cref="ReflexionArquitecturaHelper.TiposQueNombranTipoEnIl"/>),
    /// sin excluir la composición, así que la lista nombra además a quien implementa cada puerto y al registro que lo
    /// resuelve. Program.cs no aparece: no los pide.
    /// </summary>
    [Fact]
    public void Ningun_cuerpo_de_metodo_pide_el_lector_ni_el_almacen_de_originales_fuera_de_sus_consumidores()
    {
        NombranEnIl(typeof(IAlmacenOriginalesInbound)).Should().BeEquivalentTo(
            [
                .. ConsumidoresAutorizadosDelAlmacenDeOriginales,
                // La implementación (ADR-013 M2, K-3) y su registro.
                typeof(CaeManager.Infrastructure.Inbound.FileStorage.AdaptadorAlmacenOriginalesInbound),
                typeof(CaeManager.Infrastructure.DependencyInjection.InfrastructureServiceCollectionExtensions),
            ],
            "abrir un original de otro Tenant solo es legítimo después de la autoridad de la lectura; pedir el puerto dentro " +
            "de un método lo saltaría igual que inyectarlo");

        NombranEnIl(typeof(ILectorDocumentosInbound)).Should().BeEquivalentTo(
            [
                .. ConsumidoresAutorizados,
                typeof(CaeManager.Infrastructure.Inbound.Autorizacion.LectorDocumentosInboundEnBase),
                typeof(CaeManager.Infrastructure.DependencyInjection.InfrastructureServiceCollectionExtensions),
            ],
            "el lector entre Tenants sin la cartera solo lo piden los handlers que la deciden antes");
    }

    [Fact]
    public void Control_el_escaneo_de_cuerpos_ve_un_puerto_pedido_dentro_de_un_metodo()
    {
        Type[] sinteticos =
        [
            typeof(ConsumidorSinteticoDeOriginalesInbound),
            typeof(ConsumidorSinteticoDeOriginalesInboundEnLambda),
            typeof(ConsumidorSinteticoDeOriginalesInboundAsincrono),
        ];
        var ensamblado = typeof(ConsumidorSinteticoDeOriginalesInbound).Assembly;

        ReflexionArquitecturaHelper.TiposQueNombranTipoEnIl(ensamblado, typeof(IAlmacenOriginalesInbound))
            .Should().Contain(sinteticos, "si dejara de ver un cuerpo, una lambda o un método async, el test de arriba pasaría por ceguera");
        ReflexionArquitecturaHelper.TiposQueReferencianTipo(ensamblado, typeof(IAlmacenOriginalesInbound)).Select(Exterior)
            .Should().NotContain(sinteticos, "es la vía que el escaneo de firmas no ve: por eso existe el de cuerpos");
        // Y en la lambda y el async, la petición no está en el tipo exterior sino en uno anidado que genera el compilador.
        sinteticos.Skip(1).Should().OnlyContain(t => !ReferenciasIlNombra(t, typeof(IAlmacenOriginalesInbound)),
            "si el exterior ya la nombrara, el control no probaría que se recorren los tipos anidados");
    }

    private static bool ReferenciasIlNombra(Type tipo, Type nombrado) => ReferenciasIl.De(tipo).Contains(nombrado);

    private static List<Type> NombranEnIl(Type puerto) => EnsambladosEscaneados
        .Select(ReflexionArquitecturaHelper.CargarAssembly)
        .SelectMany(a => ReflexionArquitecturaHelper.TiposQueNombranTipoEnIl(a, puerto))
        .Distinct()
        .ToList();

    // Los tipos que genera el compilador (máquinas de estado, clausuras) se cuentan como su tipo exterior.
    private static Type Exterior(Type tipo)
    {
        while (tipo.DeclaringType is not null)
            tipo = tipo.DeclaringType;
        return tipo;
    }
}
