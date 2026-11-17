using System.Reflection;
using System.Text.RegularExpressions;
using CaeManager.Architecture.Tests.Soporte;
using CaeManager.Web.Services.Autorizacion;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Routing;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Decisión O-1 (b) (Chris, 2026-11-17): con papel Inbound, el Núcleo y Facturación quedan cerrados por defecto y solo
/// se abre lo de la tabla acordada con Inbound. El handler (<see cref="NucleoCerradoConPapelInboundAuthorizationHandler"/>)
/// cierra todo lo que no lleva la marca de abierta; este test fija la tabla: toda página con sesión que el registro de
/// módulos de ADR-013 (<see cref="RegistroDeModulos"/>) asigna al Núcleo o a Facturación está decidida aquí, abierta
/// (con su motivo y su marca) o cerrada, así que una página nueva sin decidir, o quitar la marca a una abierta, da rojo.
/// Para los endpoints mínimos, que no tienen tipo que marcar, exige que toda clase del Núcleo o de Facturación se mapee
/// en el grupo de <c>Program.cs</c> que los cierra, y que las marcas de abierto sean exactamente las de la tabla.
///
/// <para>
/// Mide por reflexión sobre el ensamblado compilado y por fuente para <c>Program.cs</c> y los endpoints. Lo que no
/// demuestra: que el handler niegue en ejecución (eso lo prueban <c>NucleoCerradoConPapelInboundAuthorizationHandlerTests</c>)
/// ni que el grupo de prefijo vacío añada su metadato a cada endpoint (comportamiento de ASP.NET; lo prueba la integración).
/// </para>
/// </summary>
public class NucleoPorPapelInboundTests
{
    private const string MotivoCuentaPropia = "La cuenta propia, no los datos de un Tenant: la tabla de O-1 (b) la abre.";
    private const string MotivoSesion = "La sesión la necesita con cualquier papel (tabla de O-1 (b)).";

    /// <summary>Las páginas abiertas con papel Inbound, con su motivo. Llevan <see cref="AbiertaConPapelInboundAttribute"/>.</summary>
    private static readonly Dictionary<string, string> Abiertas = new()
    {
        ["CaeManager.Web.Components.Account.Pages.CambiarContrasena"] = MotivoCuentaPropia,
        ["CaeManager.Web.Components.Account.Pages.ConfigurarAutenticadorDosFactores"] = MotivoCuentaPropia,
        ["CaeManager.Web.Features.Usuarios.Pages.MiAvatar"] =
            MotivoCuentaPropia + " ElegirAvatarPropioCommand es de autoservicio y solo toca el avatar de la propia cuenta.",
        ["CaeManager.Web.Components.Account.Pages.PendienteDeRol"] = MotivoSesion,
        ["CaeManager.Web.Components.Pages.AccesoDenegado"] =
            MotivoSesion + " Es a donde lleva la denegación: cerrarla dejaría la propia denegación sin página.",
        ["CaeManager.Web.Features.Cumplimiento.DocumentoContractual"] = MotivoSesion + " Es el texto legal aceptado.",
        ["CaeManager.Web.Features.Dashboard.Pages.Inicio"] =
            "Outbound exenta de [PaginaDeOutbound] (PaginasDeOutboundPorPapelTests): la raíz \"/\". Se mantiene abierta, " +
            "como estaba, hasta que se decida dónde aterriza el papel Inbound; sus datos pasan por AlcanceDatosService.",
    };

    /// <summary>Las páginas con sesión del Núcleo y de Facturación que se cierran con papel Inbound (tabla de O-1 (b)).</summary>
    private static readonly HashSet<string> Cerradas =
    [
        "CaeManager.Web.Features.ApiKeys.Pages.ClavesApi",
        "CaeManager.Web.Features.ApiKeys.Pages.ClavesApiOrganizacion",
        "CaeManager.Web.Features.Auditoria.Pages.AccesosDocumentosSensibles",
        "CaeManager.Web.Features.Auditoria.Pages.Auditoria",
        "CaeManager.Web.Features.AuditoriaIa.Pages.AuditoriaIa",
        "CaeManager.Web.Features.Calendario.Pages.Calendario",
        "CaeManager.Web.Features.Centros.Pages.CentroDetalle",
        "CaeManager.Web.Features.Centros.Pages.Centros",
        "CaeManager.Web.Features.Comercial.Pages.EstadoComercial",
        "CaeManager.Web.Features.Configuracion.Pages.Configuracion",
        "CaeManager.Web.Features.Configuracion.Pages.OrdenMenuLateral",
        "CaeManager.Web.Features.Configuracion.Pages.OrganizacionLogo",
        "CaeManager.Web.Features.Configuracion.Pages.SeleccionarClienteLecturaIa",
        "CaeManager.Web.Features.ContactoSeguridad.Pages.ContactosSeguridadPlataforma",
        "CaeManager.Web.Features.Delegaciones.Pages.Delegaciones",
        "CaeManager.Web.Features.Documentos.Pages.DocumentoDetalle",
        "CaeManager.Web.Features.Documentos.Pages.Documentos",
        "CaeManager.Web.Features.Documentos.Pages.ImportarDocumentos",
        "CaeManager.Web.Features.Documentos.Pages.RevisionIa",
        "CaeManager.Web.Features.Documentos.Pages.SubidaMasiva",
        "CaeManager.Web.Features.Documentos.Pages.TipoDocumentoDetalle",
        "CaeManager.Web.Features.Empresas.Pages.DeteccionTrabajadores",
        "CaeManager.Web.Features.Empresas.Pages.EmpresaDetalle",
        "CaeManager.Web.Features.Empresas.Pages.Empresas",
        "CaeManager.Web.Features.Empresas.Pages.ReclamarParaAcceso",
        "CaeManager.Web.Features.Facturacion.Pages.Facturacion",
        "CaeManager.Web.Features.GestionRoles.Pages.Roles",
        "CaeManager.Web.Features.HerramientasPdf.Pages.DividirPdf",
        "CaeManager.Web.Features.HerramientasPdf.Pages.FotoAPdf",
        "CaeManager.Web.Features.HerramientasPdf.Pages.InicioHerramientasPdf",
        "CaeManager.Web.Features.HerramientasPdf.Pages.UnirPdf",
        "CaeManager.Web.Features.IncidentesSeguridad.Pages.IncidenteSeguridadPlataforma",
        "CaeManager.Web.Features.Integraciones.Pages.Buzones",
        "CaeManager.Web.Features.Integraciones.Pages.Conexiones",
        "CaeManager.Web.Features.Plantillas.Pages.ConfigurarPlantilla",
        "CaeManager.Web.Features.Plantillas.Pages.DocumentosGenerados",
        "CaeManager.Web.Features.Plantillas.Pages.Plantillas",
        "CaeManager.Web.Features.Plataforma.Pages.Plataforma",
        "CaeManager.Web.Features.Retencion.Pages.Retencion",
        "CaeManager.Web.Features.TiposDocumento.Pages.TiposDocumento",
        "CaeManager.Web.Features.Trabajadores.Pages.TrabajadorDetalle",
        "CaeManager.Web.Features.Trabajadores.Pages.Trabajadores",
        "CaeManager.Web.Features.Usuarios.Pages.MiFirma",
        "CaeManager.Web.Features.Usuarios.Pages.Usuarios",
        "CaeManager.Web.Features.Vehiculos.Pages.VehiculoDetalle",
        "CaeManager.Web.Features.Vehiculos.Pages.Vehiculos",
    ];

    private static readonly Lazy<IReadOnlyList<Type>> TiposWeb = new(() =>
        ReflexionArquitecturaHelper.TiposDe(ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Web")).ToList());

    private static IEnumerable<Type> Paginas => TiposWeb.Value
        .Where(t => !t.IsAbstract && t.IsDefined(typeof(RouteAttribute), inherit: false));

    private static Modulo? ModuloDe(Type tipo) => RegistroDeModulos.Versionado().Clasificar(tipo)?.Modulo;

    private static bool EsDelNucleoOFacturacion(Type tipo) => ModuloDe(tipo) is Modulo.Nucleo or Modulo.Facturacion;

    /// <summary>Las que pasan por una evaluación de autorización: sin <c>[AllowAnonymous]</c>.</summary>
    private static List<Type> PaginasConSesionDelNucleoOFacturacion() => Paginas
        .Where(p => EsDelNucleoOFacturacion(p) && !p.IsDefined(typeof(AllowAnonymousAttribute), inherit: false))
        .ToList();

    [Fact]
    public void Toda_pagina_con_sesion_del_Nucleo_o_de_Facturacion_esta_decidida()
    {
        var paginas = PaginasConSesionDelNucleoOFacturacion();

        // Control positivo del instrumento: 52 páginas con sesión del Núcleo y de Facturación en origin/main al decidir
        // O-1 (b) (46 cerradas y 6 abiertas). Si el registro o la reflexión dejaran de verlas, lo de abajo pasaría vacío.
        paginas.Should().HaveCountGreaterThanOrEqualTo(52);

        paginas
            .Select(p => p.FullName!)
            .Where(n => !Abiertas.ContainsKey(n) && !Cerradas.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Should().BeEmpty(
                "una página nueva del Núcleo o de Facturación se decide aquí: cerrada con papel Inbound (lo normal, el " +
                "handler ya la niega) o abierta con [AbiertaConPapelInbound] y su motivo");
    }

    [Fact]
    public void Las_decididas_existen_y_son_del_Nucleo_o_de_Facturacion_salvo_Inicio()
    {
        var porNombre = Paginas.ToDictionary(p => p.FullName!);

        foreach (var nombre in Abiertas.Keys.Concat(Cerradas))
        {
            porNombre.Should().ContainKey(nombre, "una decisión sobre una página que ya no existe se retira");
            if (nombre == "CaeManager.Web.Features.Dashboard.Pages.Inicio")
                ModuloDe(porNombre[nombre]).Should().Be(Modulo.Outbound);
            else
                EsDelNucleoOFacturacion(porNombre[nombre]).Should().BeTrue($"{nombre} solo se decide aquí si es del Núcleo o de Facturación");
        }

        Abiertas.Keys.Intersect(Cerradas).Should().BeEmpty();
        Abiertas.Values.Should().OnlyContain(m => !string.IsNullOrWhiteSpace(m));
    }

    [Fact]
    public void La_marca_de_abierta_la_llevan_exactamente_las_abiertas()
    {
        TiposWeb.Value
            .Where(t => t.IsDefined(typeof(AbiertaConPapelInboundAttribute), inherit: false))
            .Select(t => t.FullName!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .Should().Equal(Abiertas.Keys.OrderBy(n => n, StringComparer.Ordinal),
                "quitar la marca de una abierta la cierra con papel Inbound, y ponerla en otra la abre sin decisión");
    }

    [Fact]
    public void El_handler_cierra_exactamente_las_cerradas()
    {
        // Ata el criterio en ejecución (marca, Outbound e Inbound por espacio de nombres) a la tabla de arriba.
        PaginasConSesionDelNucleoOFacturacion()
            .Where(NucleoCerradoConPapelInboundAuthorizationHandler.SeCierraConPapelInbound)
            .Select(p => p.FullName!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .Should().Equal(Cerradas.OrderBy(n => n, StringComparer.Ordinal));

        Paginas.Where(p => ModuloDe(p) == Modulo.Inbound)
            .Should().NotBeEmpty("control positivo: hay pantallas Inbound que el handler deja a IAutorizacionInbound")
            .And.OnlyContain(p => !NucleoCerradoConPapelInboundAuthorizationHandler.SeCierraConPapelInbound(p));
    }

    [Fact]
    public void Inbound_por_espacio_de_nombres_es_lo_mismo_que_Inbound_en_el_registro()
    {
        // El handler no lee el registro en ejecución: reconoce Inbound por su espacio de nombres. Si los dos criterios
        // divergieran, una pantalla Inbound se cerraría con su propio papel, o una del Núcleo se abriría.
        TiposWeb.Value
            .Where(t => (ModuloDe(t) == Modulo.Inbound) != NucleoCerradoConPapelInboundAuthorizationHandler.EsDeInbound(t))
            .Select(t => t.FullName)
            .Should().BeEmpty();
    }

    /// <summary>
    /// Métodos <c>Map…</c> del Núcleo o de Facturación que se llaman fuera del grupo, con su motivo: no tienen sesión.
    /// </summary>
    private static readonly Dictionary<string, string> MapeosFueraDelGrupo = new()
    {
        ["MapWebhookMicrosoft365Endpoints"] = "Webhook anónimo (AllowAnonymous): sin sesión ni papel.",
        ["MapWebhookWhatsAppEndpoints"] = "Webhook anónimo (AllowAnonymous): sin sesión ni papel.",
        ["MapWebhookStripeEndpoints"] = "Webhook anónimo (AllowAnonymous): sin sesión ni papel.",
        ["MapTokenIntegracionEndpoint"] = "/api/v1/token, anónimo: quien pide el token aún no lo tiene.",
        ["MapRecursosApiV1"] = "API v1: la autentica la Clave de Integración (política ApiPublica), sin sesión web ni papel.",
    };

    private const string Grupo = "nucleoYFacturacion";

    [Fact]
    public void Toda_clase_de_endpoints_del_Nucleo_o_de_Facturacion_se_mapea_en_el_grupo_que_la_cierra()
    {
        var metodosMap = TiposWeb.Value
            .Where(t => t.IsAbstract && t.IsSealed)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.Name.StartsWith("Map", StringComparison.Ordinal)
                        && m.GetParameters() is [{ ParameterType: var p }, ..] && p == typeof(IEndpointRouteBuilder))
            .ToLookup(m => m.Name, m => m.DeclaringType!);

        var programa = SinComentarios(Path.Combine(FuentesDeSrc.RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));
        var llamadas = Regex.Matches(programa, @"(?m)^\s*(\w+)\.(Map\w+)\(")
            .Select(m => (Receptor: m.Groups[1].Value, Metodo: m.Groups[2].Value))
            .Where(l => metodosMap.Contains(l.Metodo))
            .ToList();

        bool DelNucleo(string metodo) => metodosMap[metodo].Any(EsDelNucleoOFacturacion);

        // Control positivo: las 18 clases del Núcleo y de Facturación que se mapearon en el grupo al decidir O-1 (b).
        llamadas.Count(l => l.Receptor == Grupo).Should().BeGreaterThanOrEqualTo(18);

        llamadas
            .Where(l => DelNucleo(l.Metodo) && l.Receptor != Grupo && !MapeosFueraDelGrupo.ContainsKey(l.Metodo))
            .Select(l => $"{l.Receptor}.{l.Metodo}")
            .Should().BeEmpty(
                "un endpoint del Núcleo o de Facturación fuera del grupo se abre con papel Inbound: mapéalo en " +
                $"{Grupo} o declara aquí por qué no tiene sesión");

        llamadas
            .Where(l => l.Receptor == Grupo && !DelNucleo(l.Metodo))
            .Select(l => l.Metodo)
            .Should().BeEmpty("el grupo es del Núcleo y de Facturación; Outbound se cierra con .DeOutbound()");

        MapeosFueraDelGrupo.Keys
            .Where(k => !llamadas.Any(l => l.Metodo == k && l.Receptor != Grupo) || !DelNucleo(k))
            .Should().BeEmpty("una excepción obsoleta se retira");
    }

    /// <summary>Los endpoints abiertos con papel Inbound (tabla de O-1 (b)): fichero y número de rutas.</summary>
    private static readonly Dictionary<string, int> EndpointsAbiertos = new()
    {
        // Cerrar sesión, cerrar sesión en todos los dispositivos y /sesion/actividad.
        ["IdentityEndpointsExtensions.cs"] = 3,
        ["IdiomaEndpoints.cs"] = 1,
        // Para volver a cambiar de papel.
        ["ClienteActivoEndpoints.cs"] = 1,
        // Lo pinta el selector de papel.
        ["LogoTenantEndpoints.cs"] = 1,
    };

    [Fact]
    public void Los_endpoints_abiertos_son_exactamente_los_de_la_tabla()
    {
        var marcas = Directory
            .GetFiles(Path.Combine(FuentesDeSrc.RaizDelRepositorio(), "src", "CaeManager.Web"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (Fichero: Path.GetFileName(f), Veces: Regex.Matches(SinComentarios(f), @"\.AbiertoConPapelInbound\(\)").Count))
            .Where(x => x.Veces > 0)
            .ToDictionary(x => x.Fichero, x => x.Veces);

        marcas.Should().BeEquivalentTo(EndpointsAbiertos,
            "quitar .AbiertoConPapelInbound() cierra una ruta de la sesión con papel Inbound, y ponerlo en otra la abre " +
            "sin decisión");
    }

    [Fact]
    public void El_handler_esta_registrado_con_ambito()
    {
        var programa = File.ReadAllText(Path.Combine(FuentesDeSrc.RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));

        programa.Should().MatchRegex(
            @"AddScoped<Microsoft\.AspNetCore\.Authorization\.IAuthorizationHandler,\s*CaeManager\.Web\.Services\.Autorizacion\.NucleoCerradoConPapelInboundAuthorizationHandler>\(\)");
        programa.Should().MatchRegex($@"var {Grupo} = app\.MapGroup\(string\.Empty\)\.DelNucleoOFacturacion\(\);");
    }

    /// <summary>La fuente sin las líneas de comentario ni lo que sigue a «//» en una línea de código.</summary>
    private static string SinComentarios(string ruta) =>
        string.Join('\n', File.ReadAllLines(ruta)
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.TrimStart().StartsWith("///", StringComparison.Ordinal))
            .Select(l => l.Contains(" // ", StringComparison.Ordinal) ? l[..l.IndexOf(" // ", StringComparison.Ordinal)] : l));
}
