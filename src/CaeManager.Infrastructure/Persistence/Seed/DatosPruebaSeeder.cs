using CaeManager.Application.Common;
using System.Security.Cryptography;
using System.Text;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Application.Dashboard.Catalogo;
using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Facturacion;
using CaeManager.Domain.Gestiones;
using CaeManager.Domain.Incidencias;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Reclamaciones;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Siembra una cartera con la forma que espera el cliente fundador
/// (rediseñada 2026-08-01, antes eran 200 clientes genéricos pensados para
/// un único tenant): clientes con 5-10 empresas contratistas, empresas con
/// 1-3 centros y 5-12 trabajadores, cada uno con su documentación estándar
/// en una mezcla realista de estados (vigente/próximo/urgente/vencido), más
/// los datos que ejercitan el resto de flujos — asignaciones coherentes (un
/// trabajador solo trabaja en centros de su propia empresa), visitas,
/// evaluaciones, incidencias, vehículos con su documentación, proyectos con
/// sus técnicos, gestiones documentales y tarifas de facturación de los 7
/// conceptos — y un grupo de "veteranos" dados de baja hace más de cinco
/// años cuyos datos son exactamente lo que el barrido de retención
/// (`DeteccionPurgaService`) debe proponer purgar.
///
/// Trabajadores, Clientes, Empresas y lugares llevan nombres de caricaturas
/// de los 90 y 2000 — a petición del propietario del producto: nada en esta
/// siembra debe poder confundirse con una empresa o persona real.
///
/// Multi-tenant: este seeder siembra SIEMPRE dentro del tenant ambiente que
/// establezca quien lo invoque (ver <see cref="DelegacionDemoSeeder"/>, que
/// lo llama para cada tenant Cliente Delegante de demo). El guard de "ya hay
/// Clientes" también es por tenant, porque el filtro global lo acota solo.
///
/// Apagado por defecto: solo se ejecuta si <c>DatosPrueba:Activo</c> es
/// <c>true</c>, y solo la primera vez por tenant.
/// </summary>
public static class DatosPruebaSeeder
{
    private const string PrefijoEmailPrueba = "prueba.";
    private const string DominioEmailPrueba = "@caemanager.local";
    public const string ContrasenaUsuariosPrueba = "Prueba#2026";

    /// <summary>
    /// Claves API de demo en claro — sintéticas y públicas a propósito
    /// (mismo criterio que <see cref="ContrasenaUsuariosPrueba"/>): permiten
    /// llamar a la API pública V1 contra la siembra sin pasar por
    /// /configuracion/claves-api. Solo el hash SHA256 se persiste.
    /// </summary>
    public const string ClaveApiDemoActiva =
        "hydra_dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    public const string ClaveApiDemoRevocada =
        "hydra_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    /// <summary>
    /// Resumen para componer el log y para que el invocador (DelegacionDemoSeeder)
    /// sepa qué se sembró en cada tenant.
    /// </summary>
    public sealed record ResumenSiembra(
        int Clientes, int Empresas, int Subcontratas, int Centros, int Trabajadores, int Documentos);

    /// <summary>
    /// Los 9 "Cliente" (dueños de centro) de la cartera de demo — todos
    /// empresas de ficción de caricaturas, para que ningún nombre pueda
    /// coincidir con una compañía real.
    /// </summary>
    private static readonly string[] RazonesSocialesClientes =
    [
        "Acme S.A.",                               // Looney Tunes
        "Central Nuclear de Springfield S.A.",     // Los Simpson
        "Cantera Rocavista S.L.",                  // Los Picapiedra
        "Industrias McPato S.A.",                  // DuckTales
        "Corporación MomCorp S.L.",                // Futurama
        "Malvada Industria Doofenshmirtz S.A.",    // Phineas y Ferb
        "Empresas Wayne S.L.",                     // Batman: la serie animada
        "LexCorp Iberia S.A.",                     // Superman: la serie animada
        "Laboratorios Chemical X S.L."             // Las Chicas Superpoderosas
    ];

    /// <summary>
    /// Marca de ficción que se combina con el sector para nombrar cada
    /// Empresa contratista (ver <see cref="SectoresEmpresa"/>) — ninguna
    /// Empresa se llama solo por su sector y ciudad, que sí podría coincidir
    /// con una contrata real.
    /// </summary>
    private static readonly string[] MarcasCaricatura =
    [
        "Acme", "MomCorp", "Wayne", "LexCorp", "McPato", "Doofenshmirtz", "Rocavista", "Chemical X",
        "Voltron", "Planet Express", "Higher for Hire", "Krusty Krab", "Cerebro", "Umbrella Academy",
        "Cyberslug", "Gargamel"
    ];

    private static readonly string[] SectoresEmpresa =
    [
        "Limpiezas", "Mantenimiento", "Seguridad", "Logistica", "Construcciones", "Instalaciones",
        "Climatizacion", "Electricidad", "Obras", "Servicios Integrales", "Jardineria", "Facility"
    ];

    private static readonly string[] SufijosSociedad = ["S.L.", "S.A.", "S.L.U."];

    private static readonly string[] NombresCentro =
    [
        "Planta", "Almacen", "Fabrica", "Delegacion", "Oficina Central", "Nave", "Sede", "Terminal", "Deposito"
    ];

    /// <summary>
    /// Lugares de ficción (en vez de ciudades españolas reales) para nombrar
    /// Centros y direcciones — mismo motivo que <see cref="MarcasCaricatura"/>.
    /// </summary>
    private static readonly string[] Ciudades =
    [
        "Springfield", "Fondo de Bikini", "Ciudad Gotica", "Ciudad Townsville", "Retroville",
        "Peach Creek", "South Park", "Nueva Nueva York", "Amity Park", "Dimmsdale"
    ];

    /// <summary>Personajes de caricatura, para nombres de calle en direcciones de Centro.</summary>
    private static readonly string[] Apellidos =
    [
        "Simpson", "Picapiedra", "Esponja", "Turner", "Neutron", "Possible", "Gadget", "Flanders", "Utonio", "Cangrejo"
    ];

    /// <summary>
    /// La documentación estándar que el cliente fundador exige por trabajador
    /// — nombres exactos del catálogo (<see cref="TipoDocumentoSeedData"/>).
    /// </summary>
    private static readonly string[] DocumentacionEstandarTrabajador =
    [
        "Apto médico laboral",
        "EPIS (firma)",
        "Reciclaje 4h",
        "Formación Art. 19",
        "Formación 60h (base convenio)",
        "Contrato de Trabajo",
        "Alta en Seguridad Social"
    ];

    /// <summary>Caricaturas de los 90 y 2000 — (nombre, apellidos).</summary>
    private static readonly (string Nombre, string Apellidos)[] NombresTrabajadores =
    [
        // Los Simpson / Futurama
        ("Bart", "Simpson"), ("Lisa", "Simpson"), ("Homer", "Simpson"), ("Marge", "Simpson"),
        ("Milhouse", "Van Houten"), ("Nelson", "Muntz"), ("Ned", "Flanders"), ("Ralph", "Gorgory"),
        ("Philip", "Fry"), ("Turanga", "Leela"), ("Amy", "Wong"), ("Hermes", "Conrad"),
        // Nickelodeon
        ("Tommy", "Pickles"), ("Angelica", "Pickles"), ("Chuckie", "Finster"), ("Phil", "DeVille"),
        ("Lil", "DeVille"), ("Susie", "Carmichael"), ("Arnold", "Shortman"), ("Helga", "Pataki"),
        ("Gerald", "Johanssen"), ("Phoebe", "Heyerdahl"), ("Doug", "Narinas"), ("Patti", "Mayonesa"),
        ("Skeeter", "Valentine"), ("Rocko", "Wallaby"), ("Heffer", "Wolfe"), ("Filburt", "Tortuga"),
        ("Norbert", "Castor"), ("Daggett", "Castor"), ("Bob", "Esponja"), ("Patricio", "Estrella"),
        ("Arenita", "Mejillas"), ("Calamardo", "Tentaculos"), ("Eugenio", "Cangrejo"), ("Sheldon", "Plankton"),
        ("Danny", "Fenton"), ("Sam", "Manson"), ("Tucker", "Foley"), ("Jazz", "Fenton"),
        ("Timmy", "Turner"), ("Trixie", "Tang"), ("Chester", "McBadbat"), ("Jimmy", "Neutron"),
        ("Cindy", "Vortex"), ("Carl", "Wheezer"), ("Sheen", "Estevez"), ("Libby", "Folfax"),
        // Cartoon Network
        ("Johnny", "Bravo"), ("Dexter", "McPherson"), ("Dee Dee", "McPherson"), ("Mandark", "Astronomonov"),
        ("Bombon", "Utonio"), ("Burbuja", "Utonio"), ("Bellota", "Utonio"),
        ("Coraje", "Bagge"), ("Muriel", "Bagge"), ("Justo", "Bagge"),
        ("Nigel", "Uno"), ("Hoagie", "Gilligan"), ("Kuki", "Sanban"), ("Wallabee", "Beatles"),
        ("Abigail", "Lincoln"), ("Ben", "Tennyson"), ("Gwen", "Tennyson"), ("Kevin", "Levin"),
        ("Eddy", "Skipper"), ("Edd", "Marion"), ("Ed", "Peach"),
        // Disney / Warner
        ("TJ", "Detweiler"), ("Ashley", "Spinelli"), ("Vince", "LaSalle"), ("Gretchen", "Grundler"),
        ("Mikey", "Blumberg"), ("Gus", "Griswald"), ("Pepper Ann", "Pearson"),
        ("Kim", "Possible"), ("Ron", "Imparable"), ("Phineas", "Flynn"), ("Ferb", "Fletcher"),
        ("Candace", "Flynn"), ("Isabella", "Garcia-Shapiro"), ("Baljeet", "Tjinder"), ("Buford", "Van Stomm"),
        ("Yakko", "Warner"), ("Wakko", "Warner"), ("Dot", "Warner"), ("Buster", "Bunny"), ("Babs", "Bunny"),
        ("Daria", "Morgendorffer"), ("Jane", "Lane"), ("Jake", "Long"), ("Lilo", "Pelekai"), ("Nani", "Pelekai"),
        // Anime de sobremesa de los 90/2000 en España
        ("Son", "Goku"), ("Son", "Gohan"), ("Son", "Goten"), ("Bulma", "Brief"),
        ("Ash", "Ketchum"), ("Gary", "Oak"), ("Serena", "Tsukino"), ("Amy", "Mizuno"),
        ("Rei", "Hino"), ("Lita", "Kino"), ("Mina", "Aino"), ("Darien", "Chiba"),
        ("Tai", "Kamiya"), ("Matt", "Ishida"), ("Sora", "Takenouchi"), ("Izzy", "Izumi"),
        ("Mimi", "Tachikawa"), ("Joe", "Kido"), ("Kari", "Kamiya"), ("Davis", "Motomiya"),
        ("Conan", "Edogawa"), ("Ran", "Mouri"), ("Kogoro", "Mouri"),
        ("Shin", "Nohara"), ("Misae", "Nohara"), ("Hiroshi", "Nohara"),
        ("Nobita", "Nobi"), ("Shizuka", "Minamoto"), ("Takeshi", "Gouda"), ("Suneo", "Honekawa"),
        // Otros
        ("Stan", "Marsh"), ("Kyle", "Broflovski"), ("Kenny", "McCormick"),
        ("Sam", "Simpson"), ("Clover", "Ewing"), ("Alex", "Vasquez"),
        ("Jeremy", "Belpois"), ("Aelita", "Schaeffer"), ("Ulrich", "Stern"),
        ("Odd", "Della Robbia"), ("Yumi", "Ishiyama"), ("Toph", "Beifong")
    ];

    /// <summary>
    /// Trabajadores dados de baja hace más de cinco años — los candidatos que
    /// el barrido de retención (`/retencion`) debe detectar, junto con su
    /// documentación vencida hace igual de tiempo.
    /// </summary>
    private static readonly (string Nombre, string Apellidos)[] VeteranosParaPurga =
    [
        ("Pedro", "Picapiedra"), ("Pablo", "Marmol"), ("Piolin", "Canario"),
        ("Silvestre", "Gato"), ("Coyote", "Wile"), ("Correcaminos", "Geococcyx")
    ];

    public static async Task SeedAsync(
        CaeManagerDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>("DatosPrueba:Activo"))
        {
            logger.LogInformation("DatosPrueba:Activo no está activado (valor leído: '{Valor}') — no se siembran datos de prueba.",
                configuration["DatosPrueba:Activo"] ?? "(sin definir)");
            return;
        }

        if (await dbContext.Clientes.AnyAsync(cancellationToken))
        {
            logger.LogInformation("DatosPrueba:Activo está en true, pero ya hay Clientes — se omite la siembra de datos de prueba.");
            return;
        }

        logger.LogInformation("Sembrando cartera de prueba (perfil cliente fundador)…");

        var aleatorio = new Random(20260801);
        var resumen = await SembrarDatosOperativosAsync(
            dbContext, aleatorio, RazonesSocialesClientes.Length, numeroEmpresas: 24, numeroSubcontratas: 8, cancellationToken);

        await SembrarUsuariosYCarteraAsync(dbContext, userManager, logger, cancellationToken);

        logger.LogInformation(
            "Datos de prueba sembrados: {Clientes} clientes, {Empresas} empresas, {Subcontratas} subcontratas, " +
            "{Centros} centros, {Trabajadores} trabajadores, {Documentos} documentos, {Usuarios} usuarios de prueba " +
            "(contraseña «{Contrasena}» para todos, email prueba.<rol><n>@caemanager.local).",
            resumen.Clientes, resumen.Empresas, resumen.Subcontratas, resumen.Centros, resumen.Trabajadores,
            resumen.Documentos, Roles.Todos.Count * 3, ContrasenaUsuariosPrueba);
    }

    /// <summary>
    /// Solo los datos operativos, sin usuarios de prueba — para tenants de
    /// demo adicionales (ver DelegacionDemoSeeder): los usuarios
    /// prueba.&lt;rol&gt; son únicos por email y pertenecen al primer tenant
    /// sembrado, así que repetirlos aquí los movería de cartera.
    /// </summary>
    public static async Task<ResumenSiembra?> SembrarSoloDatosAsync(
        CaeManagerDbContext dbContext, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (await dbContext.Clientes.AnyAsync(cancellationToken))
            return null;

        // Semilla distinta a la del tenant principal a propósito: mismos
        // generadores, datos distintos — dos tenants con datos idénticos no
        // servirían para verificar aislamiento a simple vista.
        var aleatorio = new Random(20260802);
        var resumen = await SembrarDatosOperativosAsync(
            dbContext, aleatorio, numeroClientes: 3, numeroEmpresas: 9, numeroSubcontratas: 3, cancellationToken);

        logger.LogInformation(
            "Datos operativos sembrados en tenant de demo adicional: {Clientes} clientes, {Empresas} empresas, {Trabajadores} trabajadores.",
            resumen.Clientes, resumen.Empresas, resumen.Trabajadores);

        return resumen;
    }

    private static async Task<ResumenSiembra> SembrarDatosOperativosAsync(
        CaeManagerDbContext dbContext, Random aleatorio, int numeroClientes, int numeroEmpresas,
        int numeroSubcontratas, CancellationToken cancellationToken)
    {
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var contadorDni = 10_000_000 + aleatorio.Next(1_000_000);
        var contadorCif = 1_000_000 + aleatorio.Next(1_000_000);
        var indiceNombre = 0;

        // --- Clientes: la cartera del cliente fundador ---
        var clientes = new List<Cliente>();
        for (var i = 0; i < numeroClientes; i++)
        {
            clientes.Add(new Cliente(
                razonSocial: RazonesSocialesClientes[i % RazonesSocialesClientes.Length],
                cif: GenerarCifValido(contadorCif++),
                esCritico: i is 0 or 3,
                notas: null));
        }
        dbContext.Clientes.AddRange(clientes);
        await dbContext.SaveChangesAsync(cancellationToken);

        // --- Empresas contratistas: cada cliente trabaja con 5-10 del pool ---
        var empresas = new List<Empresa>();
        for (var i = 0; i < numeroEmpresas; i++)
            empresas.Add(new Empresa($"{ElementoAleatorio(aleatorio, SectoresEmpresa)} {ElementoAleatorio(aleatorio, MarcasCaricatura)} {i + 1:D2} {ElementoAleatorio(aleatorio, SufijosSociedad)}"));
        dbContext.Empresas.AddRange(empresas);

        var empresasClientes = new List<EmpresaCliente>();
        foreach (var cliente in clientes)
        {
            var contratistas = ElementosAleatoriosUnicos(aleatorio, empresas, aleatorio.Next(5, Math.Min(11, numeroEmpresas + 1)));
            empresasClientes.AddRange(contratistas.Select(e => new EmpresaCliente(e.Id, cliente.Id)));
        }
        dbContext.EmpresasClientes.AddRange(empresasClientes);
        await dbContext.SaveChangesAsync(cancellationToken);

        // --- Centros: cada empresa opera 1-3, siempre en clientes a los que presta servicio ---
        var clientesPorEmpresa = empresasClientes
            .GroupBy(ec => ec.EmpresaId)
            .ToDictionary(g => g.Key, g => g.Select(ec => ec.ClienteId).ToList());

        var centros = new List<Centro>();
        var centrosPorEmpresa = new Dictionary<Guid, List<Centro>>();
        var numeroCentro = 1;
        foreach (var empresa in empresas)
        {
            centrosPorEmpresa[empresa.Id] = [];
            if (!clientesPorEmpresa.TryGetValue(empresa.Id, out var clienteIds) || clienteIds.Count == 0)
                continue;

            foreach (var clienteId in ElementosAleatoriosUnicos(aleatorio, clienteIds, aleatorio.Next(1, 4)))
            {
                var centro = new Centro(
                    clienteId: clienteId,
                    empresaId: empresa.Id,
                    nombre: $"{ElementoAleatorio(aleatorio, NombresCentro)} {ElementoAleatorio(aleatorio, Ciudades)} {numeroCentro:D3}",
                    codigoCentro: $"CTR-{numeroCentro++:D4}",
                    direccion: $"Calle {ElementoAleatorio(aleatorio, Apellidos)} {aleatorio.Next(1, 200)}, {ElementoAleatorio(aleatorio, Ciudades)}",
                    contacto: null,
                    contratoVigenteHasta: null);
                centros.Add(centro);
                centrosPorEmpresa[empresa.Id].Add(centro);
            }
        }
        dbContext.Centros.AddRange(centros);
        await dbContext.SaveChangesAsync(cancellationToken);

        // --- Subcontratas, vinculadas a clientes y empresas reales ---
        var subcontratas = new List<Subcontrata>();
        var empresasPorSubcontrata = new Dictionary<Guid, List<Empresa>>();
        var clientesPorSubcontrata = new Dictionary<Guid, List<Cliente>>();
        for (var i = 0; i < numeroSubcontratas; i++)
        {
            var subcontrata = new Subcontrata($"Subcontrata {ElementoAleatorio(aleatorio, MarcasCaricatura)} {ElementoAleatorio(aleatorio, SectoresEmpresa)} {i + 1:D2} {ElementoAleatorio(aleatorio, SufijosSociedad)}");
            subcontratas.Add(subcontrata);

            var empresasVinculadas = ElementosAleatoriosUnicos(aleatorio, empresas, aleatorio.Next(1, 4));
            empresasPorSubcontrata[subcontrata.Id] = empresasVinculadas;
            dbContext.SubcontratasEmpresas.AddRange(
                empresasVinculadas.Select(e => new SubcontrataEmpresa(subcontrata.Id, e.Id)));
            var clientesVinculados = ElementosAleatoriosUnicos(aleatorio, clientes, aleatorio.Next(1, 3));
            clientesPorSubcontrata[subcontrata.Id] = clientesVinculados;
            dbContext.SubcontratasClientes.AddRange(
                clientesVinculados.Select(c => new SubcontrataCliente(subcontrata.Id, c.Id)));
        }
        dbContext.Subcontratas.AddRange(subcontratas);
        await dbContext.SaveChangesAsync(cancellationToken);

        // --- Trabajadores: 5-12 por empresa, 2-5 por subcontrata ---
        var trabajadores = new List<Trabajador>();
        var trabajadoresPorEmpresa = new Dictionary<Guid, List<Trabajador>>();
        foreach (var empresa in empresas)
        {
            trabajadoresPorEmpresa[empresa.Id] = [];
            for (var i = aleatorio.Next(5, 13); i > 0; i--)
            {
                var (nombre, apellidos) = SiguienteNombre(ref indiceNombre);
                var trabajador = Trabajador.DeEmpresa(
                    empresa.Id, nombre, apellidos, GenerarDniValido(contadorDni++),
                    fechaNacimiento: hoy.AddYears(-aleatorio.Next(21, 60)).AddDays(-aleatorio.Next(0, 365)));
                trabajadores.Add(trabajador);
                trabajadoresPorEmpresa[empresa.Id].Add(trabajador);
            }
        }

        var trabajadoresPorSubcontrata = new Dictionary<Guid, List<Trabajador>>();
        foreach (var subcontrata in subcontratas)
        {
            trabajadoresPorSubcontrata[subcontrata.Id] = [];
            for (var i = aleatorio.Next(2, 6); i > 0; i--)
            {
                var (nombre, apellidos) = SiguienteNombre(ref indiceNombre);
                var trabajador = Trabajador.DeSubcontrata(
                    subcontrata.Id, nombre, apellidos, GenerarDniValido(contadorDni++),
                    fechaNacimiento: hoy.AddYears(-aleatorio.Next(21, 60)).AddDays(-aleatorio.Next(0, 365)));
                trabajadores.Add(trabajador);
                trabajadoresPorSubcontrata[subcontrata.Id].Add(trabajador);
            }
        }
        dbContext.Trabajadores.AddRange(trabajadores);
        await dbContext.SaveChangesAsync(cancellationToken);

        // --- Asignaciones coherentes: cada trabajador, en centros de su propia empresa ---
        var asignaciones = new List<Asignacion>();
        var trabajadoresPorCentro = new Dictionary<Guid, List<Trabajador>>();
        foreach (var (empresaId, plantilla) in trabajadoresPorEmpresa)
        {
            var centrosDeLaEmpresa = centrosPorEmpresa[empresaId];
            if (centrosDeLaEmpresa.Count == 0) continue;

            foreach (var trabajador in plantilla)
            {
                foreach (var centro in ElementosAleatoriosUnicos(aleatorio, centrosDeLaEmpresa, aleatorio.Next(1, 3)))
                {
                    asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, hoy.AddDays(-aleatorio.Next(30, 900))));
                    Agrupar(trabajadoresPorCentro, centro.Id, trabajador);
                }
            }
        }

        foreach (var subcontrata in subcontratas)
        {
            var centrosAlcanzables = empresasPorSubcontrata[subcontrata.Id]
                .SelectMany(e => centrosPorEmpresa[e.Id])
                .ToList();
            if (centrosAlcanzables.Count == 0) continue;

            var plantillaSubcontrata = trabajadoresPorSubcontrata[subcontrata.Id];
            for (var i = 0; i < plantillaSubcontrata.Count; i++)
            {
                var trabajador = plantillaSubcontrata[i];

                // Requerimiento global nº 1 — Subcontrata 360 (ADR-005): el
                // primer trabajador de cada subcontrata con >1 centro
                // alcanzable queda activo en dos centros a la vez, para que
                // la unión de documentación exigida (el rasgo que distingue
                // Subcontrata 360 de una lista plana de trabajadores) sea
                // visible con datos reales, no solo en los tests de
                // integración.
                var numeroCentros = i == 0 ? Math.Min(2, centrosAlcanzables.Count) : 1;
                foreach (var centro in ElementosAleatoriosUnicos(aleatorio, centrosAlcanzables, numeroCentros))
                {
                    asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, hoy.AddDays(-aleatorio.Next(30, 600))));
                    Agrupar(trabajadoresPorCentro, centro.Id, trabajador);
                }
            }
        }
        dbContext.Asignaciones.AddRange(asignaciones);
        await dbContext.SaveChangesAsync(cancellationToken);

        // --- Documentación estándar ---
        var tiposDocumento = await dbContext.TiposDocumento.ToListAsync(cancellationToken);
        var tiposTrabajadorEstandar = DocumentacionEstandarTrabajador
            .Select(nombre => tiposDocumento.Single(t => t.Nombre == nombre))
            .ToList();
        var tiposEmpresaObligatorios = tiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Empresa && t.EsObligatorio)
            .ToList();
        var tiposVehiculo = tiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Vehiculo)
            .ToList();

        var documentos = new List<Documento>();
        foreach (var trabajador in trabajadores)
        {
            foreach (var tipo in tiposTrabajadorEstandar)
                documentos.Add(CrearDocumento(aleatorio, hoy, tipo, id => Documento.DeTrabajador(trabajador.Id, id.TipoId, id.Emision, id.Vencimiento)));
        }

        foreach (var empresa in empresas)
        {
            foreach (var tipo in tiposEmpresaObligatorios)
                documentos.Add(CrearDocumento(aleatorio, hoy, tipo, id => Documento.DeEmpresa(empresa.Id, id.TipoId, id.Emision, id.Vencimiento)));
        }

        // --- Vehículos con su documentación ---
        var vehiculos = new List<Vehiculo>();
        string[] modelos = ["Furgon Master", "Camion Atego", "Furgoneta Transit", "Pickup Hilux", "Furgon Ducato"];
        foreach (var empresa in ElementosAleatoriosUnicos(aleatorio, empresas, Math.Min(10, empresas.Count)))
        {
            var vehiculo = Vehiculo.DeEmpresa(
                empresa.Id,
                $"Vehiculo {vehiculos.Count + 1:D2}",
                ElementoAleatorio(aleatorio, modelos),
                $"{aleatorio.Next(1000, 9999)}{(char)('B' + aleatorio.Next(0, 20))}{(char)('B' + aleatorio.Next(0, 20))}{(char)('B' + aleatorio.Next(0, 20))}");
            vehiculos.Add(vehiculo);

            foreach (var tipo in tiposVehiculo)
                documentos.Add(CrearDocumento(aleatorio, hoy, tipo, id => Documento.DeVehiculo(vehiculo.Id, id.TipoId, id.Emision, id.Vencimiento)));
        }
        dbContext.Vehiculos.AddRange(vehiculos);

        // --- Veteranos para la purga de retención: baja > 5 años, docs vencidos > 5 años ---
        foreach (var (nombre, apellidos) in VeteranosParaPurga)
        {
            var empresa = ElementoAleatorio(aleatorio, empresas);
            var veterano = Trabajador.DeEmpresa(empresa.Id, nombre, apellidos, GenerarDniValido(contadorDni++));
            dbContext.Trabajadores.Add(veterano);

            var centrosDeLaEmpresa = centrosPorEmpresa[empresa.Id];
            if (centrosDeLaEmpresa.Count > 0)
            {
                var alta = hoy.AddYears(-8).AddDays(-aleatorio.Next(0, 300));
                var asignacion = new Asignacion(veterano.Id, ElementoAleatorio(aleatorio, centrosDeLaEmpresa).Id, alta);
                asignacion.DarDeBaja(hoy.AddYears(-6).AddDays(-aleatorio.Next(0, 200)));
                dbContext.Asignaciones.Add(asignacion);
            }

            foreach (var tipo in ElementosAleatoriosUnicos(aleatorio, tiposTrabajadorEstandar, 3))
            {
                var vencimiento = hoy.AddYears(-6).AddDays(-aleatorio.Next(0, 200));
                documentos.Add(Documento.DeTrabajador(
                    veterano.Id, tipo.Id, vencimiento.AddMonths(-(tipo.VigenciaMeses ?? 12)),
                    tipo.AplicaVencimientoAutomatico ? vencimiento : null));
            }
        }

        dbContext.Documentos.AddRange(documentos);
        await dbContext.SaveChangesAsync(cancellationToken);

        // --- Visitas con participantes asignados al centro ---
        var visitas = new List<Visita>();
        var centrosConGente = trabajadoresPorCentro.Keys.ToList();
        for (var i = 0; i < Math.Min(24, centrosConGente.Count * 2); i++)
        {
            var centroId = ElementoAleatorio(aleatorio, centrosConGente);
            var inicio = hoy.AddDays(aleatorio.Next(-45, 46));
            // Índice, no Random: cubre los tres orígenes sin desplazar la
            // secuencia aleatoria del resto de la siembra.
            var origen = (i % 8) switch
            {
                0 => OrigenVisita.Correo,
                4 => OrigenVisita.WhatsApp,
                _ => OrigenVisita.Plataforma
            };
            var visita = new Visita(centroId, inicio, inicio.AddDays(aleatorio.Next(0, 3)), notas: null, origen);
            visitas.Add(visita);

            dbContext.VisitasTrabajadores.AddRange(
                ElementosAleatoriosUnicos(aleatorio, trabajadoresPorCentro[centroId], aleatorio.Next(1, 4))
                    .Select(t => new VisitaTrabajador(visita.Id, t.Id)));
        }
        dbContext.Visitas.AddRange(visitas);

        // --- Incidencias ---
        string[] descripcionesIncidencia =
        [
            "Trabajador sin casco en zona de carga.", "Acceso al centro sin documentación en regla.",
            "Caída leve en escalera de la nave.", "EPI deteriorado detectado en inspección.",
            "Carretilla operada sin acreditación visible."
        ];
        foreach (var centroId in ElementosAleatoriosUnicos(aleatorio, centrosConGente, Math.Min(12, centrosConGente.Count)))
        {
            var incidencia = new Incidencia(
                centroId,
                aleatorio.Next(2) == 0 ? ElementoAleatorio(aleatorio, trabajadoresPorCentro[centroId]).Id : null,
                aleatorio.Next(4) == 0 ? TipoIncidencia.Accidente : TipoIncidencia.Incumplimiento,
                (GravedadIncidencia)aleatorio.Next(3),
                hoy.AddDays(-aleatorio.Next(1, 180)),
                ElementoAleatorio(aleatorio, descripcionesIncidencia));
            if (aleatorio.Next(2) == 0) incidencia.MarcarResuelta();
            dbContext.Incidencias.Add(incidencia);
        }

        // --- Proyectos (obras) con sus técnicos: abiertos y cerrados, con
        // algún técnico dado de baja para cubrir las dos ramas de la relación ---
        string[] nombresProyecto =
        [
            "Ampliación Planta", "Reforma Nave", "Instalación Fotovoltaica",
            "Nueva Línea de Montaje", "Modernización Climatización", "Obra Muelle de Carga"
        ];
        var clientesConProyecto = new HashSet<Guid>();
        var centrosParaProyecto = ElementosAleatoriosUnicos(
            aleatorio, centros.Where(c => trabajadoresPorCentro.ContainsKey(c.Id)).ToList(),
            Math.Min(6, centrosConGente.Count));
        for (var i = 0; i < centrosParaProyecto.Count; i++)
        {
            var centro = centrosParaProyecto[i];
            var inicio = hoy.AddDays(-aleatorio.Next(60, 400));
            var proyecto = Proyecto.Crear(
                centro.ClienteId, centro.Id,
                $"{nombresProyecto[i % nombresProyecto.Length]} {i + 1:D2}",
                inicio,
                fechaFinPrevista: aleatorio.Next(2) == 0 ? inicio.AddMonths(aleatorio.Next(6, 24)) : null,
                notas: null);
            if (i % 3 == 2)
                proyecto.Cerrar(inicio.AddDays(aleatorio.Next(30, 60)));
            dbContext.Proyectos.Add(proyecto);
            clientesConProyecto.Add(centro.ClienteId);

            var tecnicos = ElementosAleatoriosUnicos(aleatorio, trabajadoresPorCentro[centro.Id], aleatorio.Next(1, 4));
            for (var j = 0; j < tecnicos.Count; j++)
            {
                var alta = inicio.AddDays(aleatorio.Next(0, 30));
                var tecnico = new ProyectoTecnico(proyecto.Id, tecnicos[j].Id, alta);
                if (j == 1)
                    tecnico.DarDeBaja(alta.AddDays(aleatorio.Next(10, 30)));
                dbContext.ProyectosTecnicos.Add(tecnico);
            }
        }

        // --- Gestiones documentales: pendientes y completadas ---
        foreach (var centroId in ElementosAleatoriosUnicos(aleatorio, centrosConGente, Math.Min(10, centrosConGente.Count)))
        {
            var gestion = new Gestion(
                ElementoAleatorio(aleatorio, trabajadoresPorCentro[centroId]).Id,
                centroId,
                ElementoAleatorio(aleatorio, tiposTrabajadorEstandar).Id,
                notas: aleatorio.Next(2) == 0 ? "Pendiente de recibir el justificante firmado." : null);
            if (aleatorio.Next(2) == 0)
                gestion.Completar();
            dbContext.Gestiones.Add(gestion);
        }

        // --- Canales de gestión documental: cómo exige cada centro su
        // documentación — un portal del catálogo (principal) y, en la mitad,
        // un canal de correo adicional ---
        var proveedoresCae = await dbContext.ProveedoresPlataformaCae
            .Where(p => p.Activo).OrderBy(p => p.Codigo).Take(4).ToListAsync(cancellationToken);
        var centrosConCanal = ElementosAleatoriosUnicos(aleatorio, centros, Math.Min(8, centros.Count));
        for (var i = 0; i < centrosConCanal.Count; i++)
        {
            var centro = centrosConCanal[i];
            if (proveedoresCae.Count > 0)
            {
                var proveedor = proveedoresCae[i % proveedoresCae.Count];
                var canalPlataforma = CanalGestionDocumental.DePlataforma(
                    centro.Id, "Gestión general", proveedor.Id,
                    $"https://portal-demo-{proveedor.Codigo}.local",
                    $"gestion.demo{i + 1}", "Contrasena#Demo1");
                canalPlataforma.MarcarComoPrincipal();
                dbContext.CanalesGestionDocumental.Add(canalPlataforma);
            }

            if (i % 2 == 0)
            {
                dbContext.CanalesGestionDocumental.Add(CanalGestionDocumental.PorEmail(
                    centro.Id, "Trabajadores extranjeros",
                    $"cae.centro{i + 1}@correo-simulado.local", "Contacto de demo"));
            }
        }

        // --- Requisitos documentales por centro. El bloqueante está
        // garantizado: ningún trabajador sembrado tiene "DNI o NIE en vigor",
        // así que ese centro queda en EstadoCentro.Bloqueado ---
        if (centrosConCanal.Count >= 3)
        {
            var tipoDniNie = tiposDocumento.Single(t => t.Nombre == "DNI o NIE en vigor");
            var tipoAptoMedico = tiposDocumento.Single(t => t.Nombre == "Apto médico laboral");
            dbContext.TiposDocumentoCentros.Add(new TipoDocumentoCentro(
                tipoDniNie.Id, centrosConCanal[0].Id, bloqueaAcceso: true));
            dbContext.TiposDocumentoCentros.Add(new TipoDocumentoCentro(
                tipoAptoMedico.Id, centrosConCanal[1].Id, periodicidadEspecialMeses: 6));
            dbContext.TiposDocumentoCentros.Add(new TipoDocumentoCentro(
                tipoAptoMedico.Id, centrosConCanal[2].Id, incluido: false));
        }

        // --- Credenciales de acceso a portales, de empresa y de subcontrata ---
        var empresasConCredencial = ElementosAleatoriosUnicos(aleatorio, empresas, Math.Min(3, empresas.Count));
        for (var i = 0; i < empresasConCredencial.Count; i++)
        {
            dbContext.CredencialesAccesoEmpresa.Add(new CredencialAccesoEmpresa(
                empresasConCredencial[i].Id,
                $"https://portal-demo-clientes.local/acceso{i + 1}",
                empresasConCredencial[i].RazonSocial,
                $"empresa.demo{i + 1}", "Contrasena#Demo1",
                i == 0 ? "Credencial compartida con el responsable de PRL." : null));
        }

        var subcontratasConCredencial = ElementosAleatoriosUnicos(aleatorio, subcontratas, Math.Min(2, subcontratas.Count));
        for (var i = 0; i < subcontratasConCredencial.Count; i++)
        {
            dbContext.CredencialesAccesoSubcontrata.Add(new CredencialAccesoSubcontrata(
                subcontratasConCredencial[i].Id,
                $"https://portal-demo-clientes.local/subacceso{i + 1}",
                subcontratasConCredencial[i].RazonSocial,
                $"subcontrata.demo{i + 1}", "Contrasena#Demo1"));
        }

        // --- Supervisión de subcontratas (ADR-005): niveles de servicio y verificaciones externas ---
        // La mitad pasa a Supervisada; las verificaciones cubren todos los
        // resultados y todos los estados del semáforo (vigente, próximo,
        // urgente, vencido, válido sin caducidad, no válido, no encontrado),
        // más un caso con historial (dos verificaciones del mismo tipo — la
        // última manda) y tipos exigidos sin verificar, que quedan solos.
        // La primera subcontrata queda Gestionada CON verificaciones: son
        // hechos registrables en ambos niveles, no exclusivos de Supervisada.
        var centrosPorCliente = centros.GroupBy(c => c.ClienteId).ToDictionary(g => g.Key, g => g.ToList());
        var tiposSupervisables = tiposDocumento
            .Where(t => t.AmbitoAplicacion is AmbitoAplicacion.Trabajador or AmbitoAplicacion.Empresa)
            .OrderBy(t => t.Orden)
            .ToList();
        // Sin FK a usuarios (los usuarios de prueba se siembran después):
        // basta un verificador ficticio estable dentro de la tanda.
        var verificadorSiembraId = Guid.NewGuid();
        var variante = 0;
        for (var indice = 0; indice < subcontratas.Count; indice++)
        {
            var subcontrata = subcontratas[indice];
            if (indice % 2 == 1)
                subcontrata.CambiarNivelServicio(NivelServicioSubcontrata.Supervisada);

            if (subcontrata.NivelServicio != NivelServicioSubcontrata.Supervisada && indice != 0)
                continue;

            var centrosDeSusClientes = clientesPorSubcontrata[subcontrata.Id]
                .Where(c => centrosPorCliente.ContainsKey(c.Id))
                .SelectMany(c => centrosPorCliente[c.Id])
                .ToList();
            if (centrosDeSusClientes.Count == 0 || tiposSupervisables.Count == 0)
                continue;

            var centroSupervisado = centrosDeSusClientes[0];
            foreach (var tipo in tiposSupervisables.Take(7))
            {
                DateOnly? validoHasta = (variante % 7) switch
                {
                    0 => hoy.AddDays(120),
                    1 => hoy.AddDays(20),
                    2 => hoy.AddDays(5),
                    3 => hoy.AddDays(-10),
                    _ => null
                };
                var resultadoVerificacion = (variante % 7) switch
                {
                    5 => ResultadoVerificacionExterna.NoValido,
                    6 => ResultadoVerificacionExterna.NoEncontrado,
                    _ => ResultadoVerificacionExterna.Valido
                };
                dbContext.VerificacionesExternaSubcontrata.Add(new VerificacionExternaSubcontrata(
                    subcontrata.Id, centroSupervisado.Id, tipo.Id,
                    hoy.AddDays(-aleatorio.Next(1, 15)), resultadoVerificacion, verificadorSiembraId, validoHasta,
                    resultadoVerificacion == ResultadoVerificacionExterna.Valido
                        ? null
                        : "Detectado en revisión del portal del titular."));
                variante++;
            }

            // Historial: una verificación posterior del primer tipo — el
            // semáforo debe calcularse con esta, no con la de arriba.
            dbContext.VerificacionesExternaSubcontrata.Add(new VerificacionExternaSubcontrata(
                subcontrata.Id, centroSupervisado.Id, tiposSupervisables[0].Id,
                hoy, ResultadoVerificacionExterna.Valido, verificadorSiembraId, hoy.AddDays(180),
                "Renovado en el portal — sustituye a la verificación anterior."));
        }

        // --- Tarifas de facturación por cliente: los 7 conceptos con ejemplar ---
        foreach (var cliente in clientes)
        {
            dbContext.TarifasCliente.Add(TarifaCliente.Crear(cliente.Id, ConceptoFacturable.TrabajadorActivo, 8m + aleatorio.Next(0, 8) + 0.50m, "EUR"));
            dbContext.TarifasCliente.Add(TarifaCliente.Crear(cliente.Id, ConceptoFacturable.DocumentoGestionado, 4m + aleatorio.Next(0, 5) + 0.75m, "EUR"));
            if (aleatorio.Next(2) == 0)
                dbContext.TarifasCliente.Add(TarifaCliente.Crear(cliente.Id, ConceptoFacturable.AltaCentro, 25m + aleatorio.Next(0, 16), "EUR"));
            if (aleatorio.Next(2) == 0)
                dbContext.TarifasCliente.Add(TarifaCliente.Crear(cliente.Id, ConceptoFacturable.VisitaTrabajadorExtranjero, 30m + aleatorio.Next(0, 21), "EUR"));
            if (clientesConProyecto.Contains(cliente.Id))
            {
                dbContext.TarifasCliente.Add(TarifaCliente.Crear(cliente.Id, ConceptoFacturable.TecnicoAsignadoProyecto, 12m + aleatorio.Next(0, 6), "EUR"));
                dbContext.TarifasCliente.Add(TarifaCliente.Crear(cliente.Id, ConceptoFacturable.GestionProyectoRealizada, 18m + aleatorio.Next(0, 10), "EUR"));
                dbContext.TarifasCliente.Add(TarifaCliente.Crear(cliente.Id, ConceptoFacturable.DiaProyectoAbierto, 3m + aleatorio.Next(0, 4) * 0.5m, "EUR"));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return new ResumenSiembra(
            clientes.Count, empresas.Count, subcontratas.Count, centros.Count,
            trabajadores.Count + VeteranosParaPurga.Length, documentos.Count);
    }

    private readonly record struct DatosDocumento(Guid TipoId, DateOnly Emision, DateOnly? Vencimiento);

    /// <summary>
    /// Reparto realista de estados: mayoría vigente, con próximos, urgentes y
    /// vencidos suficientes para que Dashboard, semáforos y alertas tengan
    /// materia en cada tenant.
    /// </summary>
    private static Documento CrearDocumento(
        Random aleatorio, DateOnly hoy, TipoDocumento tipo, Func<DatosDocumento, Documento> fabrica)
    {
        var diasHastaVencimiento = aleatorio.Next(100) switch
        {
            < 12 => -aleatorio.Next(1, 180),   // vencido
            < 22 => aleatorio.Next(1, 15),     // urgente
            < 38 => aleatorio.Next(15, 45),    // próximo
            _ => aleatorio.Next(45, 400)        // vigente
        };

        var vencimiento = tipo.AplicaVencimientoAutomatico ? hoy.AddDays(diasHastaVencimiento) : (DateOnly?)null;
        var emision = vencimiento?.AddMonths(-(tipo.VigenciaMeses ?? 12)) ?? hoy.AddDays(-aleatorio.Next(30, 400));
        if (emision > hoy) emision = hoy;

        return fabrica(new DatosDocumento(tipo.Id, emision, vencimiento));
    }

    private static async Task SembrarUsuariosYCarteraAsync(
        CaeManagerDbContext dbContext, UserManager<ApplicationUser> userManager, ILogger logger,
        CancellationToken cancellationToken)
    {
        var usuariosPorRol = new Dictionary<string, List<ApplicationUser>>();

        foreach (var rol in Roles.Todos)
        {
            usuariosPorRol[rol] = [];

            for (var i = 1; i <= 3; i++)
            {
                var email = $"{PrefijoEmailPrueba}{rol.ToLowerInvariant()}{i}{DominioEmailPrueba}";
                var existente = await userManager.FindByEmailAsync(email);
                if (existente is not null)
                {
                    usuariosPorRol[rol].Add(existente);
                    continue;
                }

                var usuario = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    NombreCompleto = $"Prueba {rol} {i}",
                    EmailConfirmed = true,
                    // Todo su propósito es poder iniciar sesión directamente
                    // con ContrasenaUsuariosPrueba — nunca deben quedar
                    // atrapados en la pantalla de cambio de contraseña.
                    DebeCambiarContrasena = false,
                    // Antes hardcodeado al tenant #1 — asumía que este
                    // seeder siempre corría para el tenant por defecto.
                    // Desde que DelegacionDemoSeeder lo invoca sellado al
                    // tenant Cliente Delegante de demo (ver ADR-004 § 5.1),
                    // estos usuarios de prueba deben quedar en ESE tenant,
                    // no en el #1 — si no, inician sesión viendo el tenant
                    // Consultora vacío en vez de los datos que se acaban de
                    // sembrar para ellos.
                    TenantId = AmbitoTenantExplicito.TenantIdActual ?? TenantSeedData.IdPorDefecto
                };

                var resultado = await userManager.CreateAsync(usuario, ContrasenaUsuariosPrueba);
                if (!resultado.Succeeded)
                {
                    logger.LogWarning(
                        "No se pudo crear el usuario de prueba {Email}: {Errores}",
                        email, string.Join(", ", resultado.Errors.Select(e => e.Description)));
                    continue;
                }

                await userManager.AddToRoleAsync(usuario, rol);
                await AceptacionTerminosSeedHelper.AceptarParaUsuarioDeSemillaAsync(dbContext, usuario.Id, cancellationToken);
                usuariosPorRol[rol].Add(usuario);
            }
        }

        // --- Cartera de prueba: sin esto, los usuarios de prueba de los
        // roles con alcance acotado (GestorCae/CoordinadorCae/Cliente) no
        // verían ningún dato — ver IAlcanceDatosService (Fase 31). Los tres
        // GestorCae de prueba reportan al primer CoordinadorCae y se
        // reparten TODOS los clientes round-robin (con 9 clientes, 3 cada
        // uno — AlcanceRolesTests depende de este reparto exacto); cada
        // usuario Cliente de prueba queda vinculado a un Cliente real
        // distinto.
        var clientes = await dbContext.Clientes.OrderBy(c => c.CreadoEnUtc).ToListAsync(cancellationToken);
        var coordinadorPrincipal = usuariosPorRol[Roles.CoordinadorCae].FirstOrDefault();
        var gestoresPrueba = usuariosPorRol[Roles.GestorCae];

        if (coordinadorPrincipal is not null)
        {
            foreach (var gestor in gestoresPrueba)
            {
                gestor.CoordinadorUsuarioId = coordinadorPrincipal.Id;
                await userManager.UpdateAsync(gestor);
            }
        }

        if (gestoresPrueba.Count > 0)
        {
            for (var i = 0; i < clientes.Count; i++)
                clientes[i].AsignarEjecutivo(gestoresPrueba[i % gestoresPrueba.Count].Id);
        }

        var clientesPrueba = usuariosPorRol[Roles.Cliente];
        for (var i = 0; i < clientesPrueba.Count && i < clientes.Count; i++)
        {
            clientesPrueba[i].ClienteId = clientes[i].Id;
            await userManager.UpdateAsync(clientesPrueba[i]);
        }

        // --- Notificaciones persistentes: la campana de los gestores no debe
        // arrancar vacía — una sin leer y una ya leída (con acción) por gestor ---
        foreach (var gestor in gestoresPrueba)
        {
            dbContext.NotificacionesUsuario.Add(new NotificacionUsuario(
                gestor.Id,
                "Cambio de cartera",
                "Se te han asignado clientes nuevos en el reparto de cartera de demo."));

            var leida = new NotificacionUsuario(
                gestor.Id,
                "Lectura IA desactivada",
                "Un cliente de tu cartera tiene tipos de documento con la lectura IA desactivada.",
                urlAccion: "/tipos-documento",
                textoAccion: "Gestionar");
            leida.MarcarComoLeida();
            dbContext.NotificacionesUsuario.Add(leida);
        }

        await SembrarReclamacionesAsync(dbContext, clientes, cancellationToken);

        // --- Claves API (activa y revocada), filtros guardados y preferencia
        // de dashboard: /configuracion/claves-api y los selectores de filtro
        // no arrancan vacíos, y la API pública V1 es llamable con la clave de
        // demo en claro (ver ClaveApiDemoActiva) ---
        var creadorClaves = coordinadorPrincipal ?? gestoresPrueba.FirstOrDefault();
        if (creadorClaves is not null)
        {
            dbContext.ClavesApi.Add(new ClaveApi(
                "Integración de demo (activa)", ClaveApiDemoActiva[..12],
                HashSha256(ClaveApiDemoActiva), creadorClaves.Id));

            var claveRevocada = new ClaveApi(
                "Integración antigua (revocada)", ClaveApiDemoRevocada[..12],
                HashSha256(ClaveApiDemoRevocada), creadorClaves.Id);
            claveRevocada.Revocar();
            dbContext.ClavesApi.Add(claveRevocada);
        }

        var gestorConPreferencias = gestoresPrueba.FirstOrDefault();
        if (gestorConPreferencias is not null)
        {
            dbContext.FiltrosGuardados.Add(new FiltroGuardado(
                gestorConPreferencias.Id, PantallasConFiltrosGuardados.Clientes, "Solo críticos",
                """{"soloCriticos":true}"""));
            dbContext.FiltrosGuardados.Add(new FiltroGuardado(
                gestorConPreferencias.Id, PantallasConFiltrosGuardados.Documentos, "Vencidos",
                """{"estado":"Vencido"}"""));
            dbContext.PreferenciasDashboardUsuario.Add(new PreferenciaDashboardUsuario(
                gestorConPreferencias.Id,
                [CatalogoKpis.TrabajadoresActivos, CatalogoKpis.SemaforoDocumental, CatalogoKpis.IncidenciasAbiertas]));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string HashSha256(string valor) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(valor))).ToLowerInvariant();

    /// <summary>
    /// Historial de lotes de reclamación ya enviados para los primeros
    /// clientes con documentación próxima a vencer — ejercita la vista previa
    /// del lote (que descarta lo reclamado hace poco) y el historial.
    ///
    /// Siembra las tres variantes que existen desde que la reclamación puede
    /// nacer como <see cref="Conversacion"/>, para poder recorrer el ciclo
    /// entero sin buzón real: (1) con conversación y respuesta del cliente
    /// dentro del mismo hilo — el caso que cierra el círculo; (2) con
    /// conversación y **sin** respuesta desde hace más de una semana — el que
    /// alimenta el seguimiento de reclamaciones sin contestar; (3) histórica
    /// sin conversación, que es como salían todas antes y como siguen saliendo
    /// cuando no hay buzón conectado.
    ///
    /// Las conversaciones se crean sin <c>AsociarConexion</c>: sembradas a mano
    /// no tienen hilo de Graph que representar (ver <see cref="Conversacion"/>).
    /// </summary>
    private static async Task SembrarReclamacionesAsync(
        CaeManagerDbContext dbContext, List<Cliente> clientes, CancellationToken cancellationToken)
    {
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var limiteVentana = hoy.AddDays(90);
        var enviadas = 0;

        foreach (var cliente in clientes)
        {
            if (enviadas == 3)
                break;
            if (cliente.EjecutivoUsuarioId is null)
                continue;

            var documentosProximos = await (
                from documento in dbContext.Documentos
                join trabajador in dbContext.Trabajadores on documento.TrabajadorId equals trabajador.Id
                join empresaCliente in dbContext.EmpresasClientes on trabajador.EmpresaId equals (Guid?)empresaCliente.EmpresaId
                where empresaCliente.ClienteId == cliente.Id
                      && documento.FechaVencimiento != null
                      && documento.FechaVencimiento > hoy
                      && documento.FechaVencimiento <= limiteVentana
                select documento.Id)
                .Take(5)
                .ToListAsync(cancellationToken);

            if (documentosProximos.Count == 0)
                continue;

            var destinatario = $"portal.cliente{enviadas + 1}@buzon-simulado.local";
            var fechaEnvio = DateTime.UtcNow.AddDays(-7 * (enviadas + 1));
            Guid? conversacionId = null;

            // Variante 3 (la última): histórica, sin conversación.
            if (enviadas < 2)
            {
                var conversacion = new Conversacion(
                    $"Documentación pendiente de {cliente.RazonSocial}", cliente.Id);
                conversacion.AgregarMensaje(
                    DireccionMensaje.Saliente, CanalConversacion.Correo, "cae@buzon-simulado.local",
                    "<p>Adjuntamos la documentación que vence próximamente. Por favor, gestiona su renovación.</p>",
                    fechaEnvio);
                conversacion.AgregarParticipante(
                    destinatario, RolParticipante.Para, TipoParticipanteOrigen.UsuarioCliente);

                // Variante 1: el cliente contesta en el MISMO hilo. Variante 2:
                // no contesta, así que el hilo se queda esperando desde hace más
                // de una semana.
                if (enviadas == 0)
                {
                    conversacion.AgregarMensaje(
                        DireccionMensaje.Entrante, CanalConversacion.Correo, destinatario,
                        "<p>Recibido. Esta semana os subimos los certificados renovados.</p>",
                        fechaEnvio.AddDays(1));
                }

                dbContext.Conversaciones.Add(conversacion);
                conversacionId = conversacion.Id;
            }

            dbContext.ReclamacionesDocumentales.Add(new ReclamacionDocumental(
                cliente.Id,
                cliente.EjecutivoUsuarioId.Value,
                destinatario,
                fechaEnvio,
                documentosProximos,
                conversacionId));
            enviadas++;
        }
    }

    private static (string Nombre, string Apellidos) SiguienteNombre(ref int indice)
    {
        var (nombre, apellidos) = NombresTrabajadores[indice % NombresTrabajadores.Length];
        // Si la plantilla supera el catálogo de caricaturas, la segunda
        // vuelta distingue homónimos — mejor "Bart Simpson II" que dos
        // Bart Simpson idénticos en un listado.
        if (indice >= NombresTrabajadores.Length) apellidos += " II";
        indice++;
        return (nombre, apellidos);
    }

    private static void Agrupar(Dictionary<Guid, List<Trabajador>> grupos, Guid clave, Trabajador trabajador)
    {
        if (!grupos.TryGetValue(clave, out var lista))
            grupos[clave] = lista = [];
        lista.Add(trabajador);
    }

    internal static string GenerarDniValido(int numero)
    {
        const string letrasControl = "TRWAGMYFPDXBNJZSQVHLCKE";
        return $"{numero:D8}{letrasControl[numero % 23]}";
    }

    /// <summary>
    /// Genera un CIF sintético con letra de organización 'B' (sociedad
    /// limitada) y dígito de control numérico válido, replicando el mismo
    /// algoritmo que <see cref="CaeManager.Domain.Common.ValidadorIdentificacion"/>.
    /// </summary>
    private static string GenerarCifValido(int numero)
    {
        var digitos = numero.ToString("D7");
        var sumaPares = 0;
        var sumaImpares = 0;
        for (var i = 0; i < digitos.Length; i++)
        {
            var num = digitos[i] - '0';
            if (i % 2 == 1)
            {
                sumaPares += num;
            }
            else
            {
                var multiplicado = num * 2;
                sumaImpares += multiplicado > 9 ? multiplicado - 9 : multiplicado;
            }
        }

        var residuo = (sumaPares + sumaImpares) % 10;
        var digitoControl = residuo == 0 ? 0 : 10 - residuo;
        return $"B{digitos}{digitoControl}";
    }

    private static T ElementoAleatorio<T>(Random aleatorio, IReadOnlyList<T> lista) => lista[aleatorio.Next(lista.Count)];

    private static List<T> ElementosAleatoriosUnicos<T>(Random aleatorio, IReadOnlyList<T> lista, int cantidad)
    {
        cantidad = Math.Min(cantidad, lista.Count);
        return lista.OrderBy(_ => aleatorio.Next()).Take(cantidad).ToList();
    }
}
