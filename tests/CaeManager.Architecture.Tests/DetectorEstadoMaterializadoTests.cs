using System.Reflection;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.EstadoMaterializado;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Trinquetes del detector de cambios del estado materializado (diseño del estado materializado, § 4.3 y § 8). El detector
/// es un interceptor de todo <c>CaeManagerDbContext</c>, así que ningún guardado por el <c>ChangeTracker</c> se lo salta;
/// estos tests cierran las tres puertas que quedan: una entidad nueva que es entrada del cálculo y nadie añade al inventario,
/// una transacción explícita nueva que no recalcula antes de confirmar, y una escritura masiva fuera del <c>ChangeTracker</c>.
/// </summary>
public class DetectorEstadoMaterializadoTests
{
    /// <summary>Propiedades que apuntan a una entrada del cálculo: una entidad con alguna es candidata a disparador.</summary>
    private static readonly string[] LlavesDeEntrada =
    [
        "CentroId", "TrabajadorId", "EmpresaId", "SubcontrataId", "TipoDocumentoId", "DocumentoId",
        "CanalGestionDocumentalId", "ProyectoId", "ClienteEmpresarialId",
    ];

    /// <summary>
    /// Entidades con <c>TenantId</c> que apuntan a una entrada del cálculo pero no entran en él (diseño § 8, «no entran»).
    /// Añadir una aquí es afirmar que ningún cambio suyo mueve el estado de un Centro, Trabajador, Empresa o par.
    /// </summary>
    private static readonly HashSet<string> NoEntran = new(StringComparer.Ordinal)
    {
        // Registro de llamadas a plataformas CAE de terceros: el cálculo no lo lee.
        "CaeManager.Domain.Integraciones.UsoConexionPlataformaCae",

        // Ninguna la leen los servicios del cálculo (RecalculoEstadoMaterializadoService, CalculoEstadoCentroService,
        // EvaluacionDeAccesoPorCentroService, CalculoEstadoDocumentalService): auditoría, autorizaciones de lectura,
        // comunicaciones, contactos, contratos de acceso, firma, revisión y caché de IA, credenciales y accesos a
        // plataformas, catálogos de Delegación y Oficio, evidencias Inbound, Gestiones, incidencias, plantillas,
        // reclamaciones, verificaciones, detecciones, vehículos y visitas. Si alguna pasa a leerse, sale de aquí y entra
        // en InventarioDisparadoresEstado.
        "CaeManager.Domain.Auditoria.RegistroAccesoDocumentoSensible",
        "CaeManager.Domain.AutorizacionesLectura.AccesoLecturaInbound",
        "CaeManager.Domain.AutorizacionesLectura.AutorizacionLecturaTrabajador",
        "CaeManager.Domain.Blindaje42.SolicitudCertificacionTgss",
        "CaeManager.Domain.Comunicaciones.Conversacion",
        "CaeManager.Domain.Comunicaciones.DetalleSugerenciaGestionCorreo",
        "CaeManager.Domain.Comunicaciones.SolicitudPrioridadDocumento",
        "CaeManager.Domain.Comunicaciones.SugerenciaVisitaCorreo",
        "CaeManager.Domain.Contactos.ContactoAgenda",
        "CaeManager.Domain.Contactos.ContactoAgendaTipoDocumento",
        "CaeManager.Domain.ContratosAcceso.ContratoAcceso",
        "CaeManager.Domain.Documentos.AprobacionDocumento",
        "CaeManager.Domain.Documentos.ConfiguracionIaDocumentoCliente",
        "CaeManager.Domain.Documentos.FirmaDigitalDocumento",
        "CaeManager.Domain.Documentos.FirmaEnCampoDocumento",
        "CaeManager.Domain.Documentos.RevisionIaDocumento",
        "CaeManager.Domain.Documentos.SelloEmpresa",
        "CaeManager.Domain.Documentos.TipoDocumentoAlias",
        "CaeManager.Domain.Documentos.VerificacionDocumentoOficial",
        "CaeManager.Domain.DocumentosIa.AuditoriaExtraccionIa",
        "CaeManager.Domain.DocumentosIa.ExtraccionIaCacheDocumento",
        "CaeManager.Domain.DocumentosIa.TrabajoAnalisisDocumento",
        "CaeManager.Domain.Empresas.AccesoPlataformaCaeEmpresa",
        "CaeManager.Domain.Empresas.CredencialAccesoEmpresa",
        "CaeManager.Domain.Empresas.DelegacionEmpresa",
        "CaeManager.Domain.Empresas.OficioEmpresa",
        "CaeManager.Domain.Empresas.PlataformaCaeClienteEmpresarial",
        "CaeManager.Domain.EvidenciasInbound.EvidenciaInbound",
        "CaeManager.Domain.Gestiones.Gestion",
        "CaeManager.Domain.Incidencias.Incidencia",
        "CaeManager.Domain.Plantillas.DocumentoGenerado",
        "CaeManager.Domain.Plantillas.ItemGeneracionDocumento",
        "CaeManager.Domain.Plantillas.PlantillaDocumento",
        "CaeManager.Domain.Proyectos.PlantillaRequisitosEventoTipo",
        "CaeManager.Domain.Reclamaciones.ReclamacionDocumental",
        "CaeManager.Domain.Reclamaciones.ReclamacionDocumentalDocumento",
        "CaeManager.Domain.Subcontratas.CredencialAccesoSubcontrata",
        "CaeManager.Domain.Subcontratas.VerificacionExternaSubcontrata",
        "CaeManager.Domain.Trabajadores.DeteccionTrabajador",
        "CaeManager.Domain.Vehiculos.Vehiculo",
        "CaeManager.Domain.Vehiculos.VehiculoTrabajador",
        "CaeManager.Domain.Visitas.Visita",
        "CaeManager.Domain.Visitas.VisitaTrabajador",
    };

    [Fact]
    public void Toda_entidad_que_apunta_a_una_entrada_del_calculo_esta_inventariada_o_declarada_fuera()
    {
        var candidatas = typeof(EntidadConTenant).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(EntidadConTenant).IsAssignableFrom(t))
            .Where(t => t.Namespace != "CaeManager.Domain.EstadoMaterializado")
            .Where(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(p => LlavesDeEntrada.Contains(p.Name)))
            .ToList();
        candidatas.Should().NotBeEmpty("si no encuentra ninguna, el instrumento está roto");

        var sinClasificar = candidatas
            .Where(t => !InventarioDisparadoresEstado.Entradas.ContainsKey(t) && !NoEntran.Contains(t.FullName!))
            .Select(t => t.FullName!)
            .Order()
            .ToList();

        string.Join(", ", sinClasificar).Should().BeEmpty(
            "una entidad que apunta a Centro, Trabajador, Empresa, Tipo o documento entra en InventarioDisparadoresEstado " +
            "(con sus raíces) o en NoEntran (con la razón): si no, sus cambios se saltan el recálculo");
    }

    [Fact]
    public void El_inventario_no_declara_fuera_lo_que_trata_como_entrada()
    {
        InventarioDisparadoresEstado.Entradas.Keys.Select(t => t.FullName!).Intersect(NoEntran).Should().BeEmpty();
        InventarioDisparadoresEstado.Entradas.Keys.Should().OnlyContain(t => typeof(EntidadConTenant).IsAssignableFrom(t));
        NoEntran.Where(n => typeof(EntidadConTenant).Assembly.GetType(n) is null).Should().BeEmpty(
            "un nombre que ya no existe en el dominio no protege nada");
    }

    /// <summary>
    /// Una transacción explícita nueva no pasaría por el recálculo antes de confirmar: los guardados que hiciera dentro se
    /// quedarían acumulados. Las que hay recalculan (las dos envolturas) o no escriben entradas del cálculo.
    /// </summary>
    [Fact]
    public void Ninguna_transaccion_explicita_fuera_de_las_envolturas()
    {
        var permitidas = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TransaccionDeComando.cs",                 // envoltura: recalcula antes del COMMIT
            "TransaccionDocumentoBloqueado.cs",        // envoltura de los comandos de Documento: recalcula antes del COMMIT
            "EscritorEstadoMaterializado.cs",          // escribe el propio estado
            "RecalculoEstadoEnLinea.cs",               // transacción propia del recálculo tras un guardado suelto
            "ProcesadorColaEstadoMaterializado.cs",    // una transacción por pasada de la cola; solo escribe el propio estado y la cola
            "CambioDeDiaEstadoMaterializado.cs",       // cotejo nocturno: foto REPEATABLE READ de solo lectura, se deshace siempre
            "ExportacionCompletaTenant.cs",            // exportación de un Tenant: foto REPEATABLE READ de solo lectura, se deshace siempre
            "PilotoOutboundSeeder.cs",                 // siembra (en el arranque y en el modo CLI, dentro del ámbito «sin recálculo», que encola sus Tenants al cerrar)
            "TrabajoAnalisisDocumentoRepository.cs",   // reclama un trabajo de IA; no toca entradas
            "EventoWebhookRepository.cs",              // registra un webhook; no toca entradas
            "TransicionLineaBaseCompactada.cs",        // migraciones, conexión propia
            "ReparacionAdjuntosTemporales.cs",         // modo manual: solo cambia rutas de adjuntos, ninguna entrada del cálculo
        };

        var fuera = FicherosDeSrc()
            .Where(f => File.ReadAllText(f).Contains("BeginTransaction", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Where(n => !permitidas.Contains(n!))
            .ToList();

        fuera.Should().BeEmpty("una transacción explícita de un comando va por ITransaccionDeComando, que recalcula el estado antes de confirmar");
    }

    [Theory]
    [InlineData("CaeManager.Infrastructure/Persistence/TransaccionDeComando.cs")]
    [InlineData("CaeManager.Infrastructure/Persistence/Repositories/TransaccionDocumentoBloqueado.cs")]
    public void Las_envolturas_recalculan_antes_de_confirmar_y_descartan_al_deshacer(string fichero)
    {
        var texto = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", fichero));
        var recalculo = texto.IndexOf("ProcesarAntesDeConfirmarAsync", StringComparison.Ordinal);
        var commit = texto.IndexOf("CommitAsync", StringComparison.Ordinal);

        recalculo.Should().BePositive("la envoltura recalcula lo detectado");
        recalculo.Should().BeLessThan(commit, "el recálculo va dentro de la transacción, antes de confirmarla");
        texto.Should().Contain(".Descartar(dbContext)", "lo detectado en una transacción deshecha no llegó a la base");
    }

    [Fact]
    public void El_detector_es_el_ultimo_interceptor_del_contexto()
    {
        var texto = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Infrastructure", "Persistence", "ConfiguracionDeContexto.cs"));
        var detector = texto.IndexOf("DetectorCambiosEstadoInterceptor", StringComparison.Ordinal);

        detector.Should().BePositive("sin el interceptor ningún guardado recalcula");
        foreach (var otro in new[] { "AuditoriaInterceptor", "TenantSelladoInterceptor", "TenantRlsConnectionInterceptor", "ConcurrenciaOptimistaInterceptor" })
            texto.IndexOf(otro, StringComparison.Ordinal).Should().BeLessThan(detector,
                $"el detector recoge después de {otro}: el TenantId sellado es el que decide el Tenant de las raíces");
    }

    /// <summary>
    /// <c>ExecuteUpdate</c> y <c>ExecuteDelete</c> se saltan el <c>ChangeTracker</c> y, con él, el detector (diseño § 4.3).
    /// Solo en siembras y migraciones (que encolan el Tenant entero) y en sitios que no tocan entradas del cálculo.
    /// </summary>
    [Fact]
    public void Nadie_escribe_fuera_del_ChangeTracker_salvo_siembras_y_lo_que_no_es_entrada()
    {
        var permitidas = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ExportacionCompletaTenant.cs",            // exportación de un Tenant: foto REPEATABLE READ de solo lectura, se deshace siempre
            "PilotoOutboundSeeder.cs",       // siembra (en el arranque y en el modo CLI, dentro del ámbito «sin recálculo», que encola sus Tenants al cerrar)
            "RetiradaTenantDemoService.cs",  // tarifas, filtros y órdenes de cajas de otro Tenant; el Tenant se retira entero
            "AvatarDeCuentasIdentity.cs",    // avatar de la cuenta de Identity
            "RegistroSesionesUsuario.cs",    // revocación de sesiones de cookie (SesionesUsuario): idempotente con «RevocadaEnUtc IS NULL»; no es entrada del cálculo
        };

        var fuera = FicherosDeSrc()
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), @"\.Execute(Update|Delete)(Async)?\s*\("))
            .Select(Path.GetFileName)
            .Where(n => !permitidas.Contains(n!))
            .ToList();

        fuera.Should().BeEmpty("una escritura masiva sobre una entrada del cálculo dejaría el estado materializado sin recalcular");
    }

    private static IEnumerable<string> FicherosDeSrc() =>
        Directory.EnumerateFiles(Path.Combine(RaizDelRepositorio(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
            " — este test necesita el árbol fuente del repositorio.");
    }
}
