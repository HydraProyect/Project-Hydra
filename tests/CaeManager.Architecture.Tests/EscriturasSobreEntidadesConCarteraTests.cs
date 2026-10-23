using System.Reflection;
using CaeManager.Application.Common;
using FluentAssertions;
using MediatR;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Segunda red de la Asignación de Cartera en escritura, más ancha que
/// <see cref="AlcanceDeCarteraEnEscrituraTests"/>: aquel solo mira handlers que
/// dependen de uno de los repositorios con cartera y cargan su agregado por Id,
/// así que un comando que llega a la fila por un <c>…QueryContext</c>, por un
/// repositorio de tabla puente (p. ej. la Documentación requerida de un Centro)
/// o que CREA algo colgado de una Empresa ajena (un Vehículo nuevo de una
/// Empresa fuera de cartera) no se evaluaba. Este test decide por el COMANDO:
/// si su contrato nombra una entidad con cartera (<see cref="Entidades"/>), su
/// handler tiene que poder preguntar por el alcance.
///
/// <para>
/// «Poder preguntar» se mide por el constructor del handler: depende de
/// <see cref="IAlcanceDatosService"/>, o de un servicio cuya implementación
/// depende de él (<see cref="ServiciosQueConsultanElAlcance"/>), o es un
/// delegador puro (IMediator/ISender sin IUnitOfWork: el comando al que delega
/// pasa por este mismo test). Que la comprobación esté de verdad antes de
/// escribir lo prueban los tests de Application de cada handler; esto solo
/// impide que el olvido total pase desapercibido en un comando nuevo.
/// </para>
///
/// <para>
/// Cartera ≠ aislamiento de Tenant ≠ privilegio de plataforma: este test no
/// mira RLS ni sesiones privilegiadas. El alcance que pide es el de
/// Operación (Asignación de Cartera); Administrador —incluido el Operador CAE
/// externo con encargo de administración, que opera con ese rol—, Dirección
/// CAE y Consulta reciben null («sin restricción») de
/// <see cref="IAlcanceDatosService"/>, así que la comprobación no les recorta nada.
/// </para>
/// </summary>
public class EscriturasSobreEntidadesConCarteraTests
{
    /// <summary>Entidades con alcance de cartera definido en <see cref="IAlcanceDatosService"/>.</summary>
    private static readonly string[] Entidades =
        ["Empresa", "Cliente", "Subcontrata", "Centro", "Trabajador", "Vehiculo", "Documento"];

    /// <summary>
    /// Handlers que no consultan el alcance, cada uno con el motivo por el que es correcto.
    /// </summary>
    private static readonly Dictionary<string, string> Exentos = new()
    {
        ["CrearUsuarioCommandHandler"] =
            "cuentas de usuario: autoridad de Propiedad (AutoridadSobreCuentas, RolesReservadosAlTenantDeOrigen), no " +
            "de Operación; IAlcanceDatosService excluye expresamente los usuarios de sus listas",
        ["EditarUsuarioCommandHandler"] = "mismo motivo que CrearUsuarioCommandHandler",
        ["CrearTarifaClienteCommandHandler"] =
            "tarifas de facturación: plano Comercial, no Operación; el propio comando exige rol Administrador o " +
            "Dirección CAE (AutorizacionTarifasCliente), que no tienen restricción de cartera",
        ["ActualizarTarifaClienteCommandHandler"] = "mismo motivo que CrearTarifaClienteCommandHandler",
        ["EliminarTarifaClienteCommandHandler"] = "mismo motivo que CrearTarifaClienteCommandHandler",
        ["RegistrarHistorialInformeCommandHandler"] =
            "anota en el historial del propio usuario qué informe generó; no escribe sobre el Cliente empresarial, y " +
            "el informe ya sale filtrado por la cartera",
        ["ConcederAutorizacionLecturaCommandHandler"] =
            "autorización de lectura del Tenant de la Empresa contratista: acto de Propiedad del Tenant propietario; " +
            "el handler exige rol efectivo Administrador (AutorizacionLecturaAutoridad), que no tiene restricción de " +
            "cartera, y los Trabajadores se comprueban contra el propio Tenant",
        ["AsignarTrabajadoresAutorizacionCommandHandler"] = "mismo motivo que ConcederAutorizacionLecturaCommandHandler",
        ["RetirarTrabajadorAutorizacionCommandHandler"] = "mismo motivo que ConcederAutorizacionLecturaCommandHandler",
        ["IniciarLoteGeneracionDocumentosCommandHandler"] =
            "solo encola el lote; cada documento lo escribe GenerarDocumentoIndividualCommandHandler, que comprueba " +
            "el alcance del Trabajador o Cliente empresarial ítem a ítem",
    };

    /// <summary>
    /// Deuda conocida: handlers que deberían consultar el alcance y todavía no lo hacen. Solo
    /// puede encoger: una entrada que ya cumple hace fallar el test para que se retire.
    /// </summary>
    private static readonly HashSet<string> Pendientes = [];

    // Red por carpeta: estos comandos pueden identificar solo la Gestión o Tarifa por Id, sin
    // nombrar el Centro o Cliente empresarial. La dependencia permite comprobar la autorización;
    // los tests de Application comprueban que se ejerce antes de escribir (incluida lectura ≠ gestión).
    private static readonly Dictionary<string, (string Carpeta, Type Dependencia)> ComandosDeGestionesYFacturacion = new()
    {
        ["CompletarGestionCommandHandler"] = ("Gestiones", typeof(IAlcanceDatosService)),
        ["CrearGestionesParaTrabajadorCommandHandler"] = ("Gestiones", typeof(IAlcanceDatosService)),
        ["EliminarGestionCommandHandler"] = ("Gestiones", typeof(IAlcanceDatosService)),
        ["RestaurarGestionCommandHandler"] = ("Gestiones", typeof(IAlcanceDatosService)),
        // Plano Comercial: AutorizacionTarifasCliente exige el rol efectivo Administrador o
        // Dirección CAE mediante ICurrentUserService; no corresponde recortarlo por cartera.
        ["CrearTarifaClienteCommandHandler"] = ("Facturacion", typeof(ICurrentUserService)),
        ["ActualizarTarifaClienteCommandHandler"] = ("Facturacion", typeof(ICurrentUserService)),
        ["EliminarTarifaClienteCommandHandler"] = ("Facturacion", typeof(ICurrentUserService)),
    };

    [Theory]
    [InlineData("Gestiones")]
    [InlineData("Facturacion")]
    public void Todo_comando_de_gestiones_o_facturacion_esta_clasificado_y_puede_comprobar_cartera_o_rol(string carpeta)
    {
        var handlers = typeof(ICommand).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .Where(t => t.Namespace?.Split('.') is [_, "Application", var area, ..] && area == carpeta)
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType
                                                   && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)
                                                   && typeof(ICommandBase).IsAssignableFrom(i.GetGenericArguments()[0])))
            .ToList();
        var clasificados = ComandosDeGestionesYFacturacion
            .Where(c => c.Value.Carpeta == carpeta)
            .ToDictionary(c => c.Key, c => c.Value.Dependencia);

        handlers.Should().NotBeEmpty("control positivo: la carpeta tiene comandos de escritura");
        string.Join(" | ", handlers.Select(h => h.Name).Except(clasificados.Keys)).Should().BeEmpty(
            "todo comando de Gestiones o Facturación debe clasificarse por su comprobación de cartera o rol");
        string.Join(" | ", clasificados.Keys.Except(handlers.Select(h => h.Name))).Should().BeEmpty(
            "las clasificaciones de comandos retirados deben eliminarse");
        string.Join(" | ", handlers.Where(h => clasificados.TryGetValue(h.Name, out var dependencia)
                                                && !Parametros(h).Contains(dependencia)).Select(h => h.Name))
            .Should().BeEmpty("cada handler debe conservar la dependencia que comprueba su cartera o rol efectivo");
    }

    [Fact]
    public void Todo_comando_que_nombra_una_entidad_con_cartera_tiene_un_handler_que_consulta_el_alcance()
    {
        var infractores = HandlersQueNoConsultanElAlcance();

        string.Join(" | ", infractores.Except(Pendientes).Except(Exentos.Keys)).Should().BeEmpty(
            "un comando que escribe sobre una Empresa, Cliente, Subcontrata, Centro, Trabajador, Vehículo o " +
            "Documento identificado en su contrato debe comprobar la Asignación de Cartera del usuario antes de " +
            "escribir (IAlcanceDatosService, p. ej. EmpresaParaGestionVisibleAsync) y responder como inexistente " +
            "fuera de ella; si de verdad no aplica, añádelo a Exentos con el motivo");

        string.Join(" | ", Pendientes.Except(infractores)).Should().BeEmpty(
            "estos handlers ya consultan el alcance: retíralos de Pendientes para que no puedan volver a perderlo");
    }

    /// <summary>
    /// Comandos de Importación con su motivo. Escriben en bloque Empresas, Clientes empresariales,
    /// Centros, Trabajadores y Documentos sacados de un fichero, sin nombrar ningún Id en su contrato,
    /// así que la red de arriba (que decide por los Ids) no los ve. No se acotan por cartera: el
    /// propio comando exige el acto de un Administrador (<see cref="IAutorizacionEscrituraEfectiva"/>:
    /// rol Administrador o Sesión Privilegiada de Aprovisionamiento sobre este Tenant), y Administrador
    /// no tiene restricción de cartera. Con encargo de administración no pleno, el área Importacion
    /// queda además excluida por <c>ActosExcluidosDelEncargo</c>.
    /// </summary>
    private static readonly Dictionary<string, string> ComandosDeImportacion = new()
    {
        ["EjecutarImportacionCommandHandler"] =
            "alta masiva desde una plantilla por entidad; solo Administrador (Importacion.SoloAdministrador)",
        ["EjecutarImportacionCombinadaCommandHandler"] =
            "alta masiva combinada (Clientes empresariales, Centros, Empresas, Trabajadores); solo Administrador",
        ["RegistrarHistorialImportacionCommandHandler"] =
            "anota la operación de importación ya ejecutada; solo Administrador",
    };

    /// <summary>
    /// Red que no depende de los Ids del contrato: todo comando de la carpeta Importacion está
    /// clasificado en <see cref="ComandosDeImportacion"/> y su handler exige el acto de un
    /// Administrador. Un comando de importación nuevo, o uno que pierda la dependencia de
    /// <see cref="IAutorizacionEscrituraEfectiva"/>, hace fallar el test.
    /// </summary>
    [Fact]
    public void Todo_comando_de_importacion_esta_clasificado_y_exige_el_acto_de_un_administrador()
    {
        var handlers = typeof(ICommand).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .Where(t => t.Namespace?.Split('.') is [_, "Application", "Importacion", ..])
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType
                                                   && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)
                                                   && typeof(ICommandBase).IsAssignableFrom(i.GetGenericArguments()[0])))
            .ToList();

        handlers.Should().NotBeEmpty("control positivo: la carpeta Importacion tiene comandos");

        string.Join(" | ", handlers.Select(h => h.Name).Except(ComandosDeImportacion.Keys)).Should().BeEmpty(
            "un comando de importación escribe en bloque entidades con cartera sin nombrarlas en su contrato: " +
            "clasifícalo en ComandosDeImportacion con su motivo");

        string.Join(" | ", ComandosDeImportacion.Keys.Except(handlers.Select(h => h.Name))).Should().BeEmpty(
            "estas entradas ya no existen: retíralas de ComandosDeImportacion");

        string.Join(" | ", handlers.Where(h => !Parametros(h).Contains(typeof(IAutorizacionEscrituraEfectiva))).Select(h => h.Name))
            .Should().BeEmpty(
                "un comando de importación no se acota por cartera, así que tiene que exigir el acto de un " +
                "Administrador (IAutorizacionEscrituraEfectiva.EsActoDeAdministradorAsync)");
    }

    private static List<string> HandlersQueNoConsultanElAlcance()
    {
        var application = typeof(ICommand).Assembly;
        var conAlcance = ServiciosQueConsultanElAlcance();

        return application.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>))
                .Select(i => new { Handler = t, Comando = i.GetGenericArguments()[0] }))
            .Where(x => typeof(ICommandBase).IsAssignableFrom(x.Comando))
            .Where(x => NombraUnaEntidadConCartera(x.Comando))
            .Where(x =>
            {
                var parametros = Parametros(x.Handler);
                if (parametros.Any(conAlcance.Contains)) return false;
                var delegaPorMediador = parametros.Contains(typeof(IMediator)) || parametros.Contains(typeof(ISender));
                return !(delegaPorMediador && !parametros.Contains(typeof(IUnitOfWork)));
            })
            .Select(x => x.Handler.Name)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// El comando identifica una entidad con cartera por <c>&lt;Entidad&gt;Id(s)</c>, o por
    /// <c>Id(s)</c> a secas cuando vive en la carpeta de esa entidad (<see cref="CarpetasConCartera"/>):
    /// <c>RestaurarDocumentoCommand(Guid Id)</c> no dice «Documento» en su contrato, pero lo es.
    /// </summary>
    private static bool NombraUnaEntidadConCartera(Type comando) =>
        comando.GetProperties().Any(p => EsIdentificador(p.PropertyType)
                                         && (Entidades.Any(e => p.Name == e + "Id" || p.Name == e + "Ids")
                                             || (p.Name is "Id" or "Ids" && EstaEnUnaCarpetaConCartera(comando))));

    /// <summary>
    /// Carpetas de Application cuyo agregado raíz tiene cartera, más Incidencias y Gestiones:
    /// cuelgan de un Centro o un Trabajador. Facturación escribe Tarifas del plano Comercial,
    /// clasificadas en Exentos y en la red por carpeta que exige comprobar el rol efectivo.
    /// </summary>
    private static readonly string[] CarpetasConCartera =
        ["Documentos", "Empresas", "Clientes", "Subcontratas", "Centros", "Trabajadores", "Vehiculos", "Incidencias", "Gestiones", "Facturacion"];

    private static bool EstaEnUnaCarpetaConCartera(Type comando) =>
        comando.Namespace?.Split('.') is [_, "Application", var carpeta, ..] && CarpetasConCartera.Contains(carpeta);

    private static bool EsIdentificador(Type tipo) =>
        tipo == typeof(Guid) || tipo == typeof(Guid?)
        || (tipo.IsGenericType && tipo.GetGenericArguments() is [var argumento]
            && (argumento == typeof(Guid) || argumento == typeof(Guid?))
            && typeof(IEnumerable<>).MakeGenericType(argumento).IsAssignableFrom(tipo));

    private static HashSet<Type> Parametros(Type tipo) =>
        tipo.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType).ToHashSet();

    /// <summary>
    /// <see cref="IAlcanceDatosService"/> y toda clase de Application o Infrastructure (y las
    /// interfaces que implementa) cuyo constructor depende de él, hasta un punto fijo: un servicio que
    /// depende de otro que consulta el alcance también cuenta.
    /// </summary>
    private static HashSet<Type> ServiciosQueConsultanElAlcance()
    {
        var clases = new[] { typeof(ICommand).Assembly, typeof(CaeManager.Infrastructure.Persistence.CaeManagerDbContext).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .ToList();

        var conAlcance = new HashSet<Type> { typeof(IAlcanceDatosService) };
        bool crecio;
        do
        {
            crecio = false;
            foreach (var clase in clases.Where(c => !conAlcance.Contains(c)))
            {
                if (!Parametros(clase).Any(conAlcance.Contains)) continue;
                conAlcance.Add(clase);
                // Un handler cuenta como clase (otro handler puede delegar en él, como
                // PrepararVistaPreviaReclamacion en los de EnviarReclamacion), pero no su
                // IRequestHandler<,>: eso marcaría a cualquiera que dependa de MediatR.
                foreach (var interfaz in clase.GetInterfaces().Where(i => i.Assembly != typeof(object).Assembly
                                                                       && i.Assembly != typeof(IMediator).Assembly))
                    conAlcance.Add(interfaz);
                crecio = true;
            }
        } while (crecio);

        return conAlcance;
    }
}
