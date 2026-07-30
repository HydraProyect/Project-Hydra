using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Genera una base de datos genérica de tamaño realista (cientos de filas
/// por entidad) para pruebas de carga básicas y para verificar que los
/// seis roles (ver Roles.cs) ven lo que deben ver con volumen real de
/// datos — no para desarrollo normal.
///
/// Apagado por defecto: solo se ejecuta si <c>DatosPrueba:Activo</c> es
/// <c>true</c> en configuración, y solo la primera vez (si ya hay algún
/// Cliente, no vuelve a sembrar) — así un redeploy no duplica datos ni
/// pisa lo que el equipo haya creado a mano.
/// </summary>
public static class DatosPruebaSeeder
{
    private const string PrefijoEmailPrueba = "prueba.";
    private const string DominioEmailPrueba = "@caemanager.local";
    public const string ContrasenaUsuariosPrueba = "Prueba#2026";

    private static readonly string[] NombresPila =
    [
        "Alvaro", "Beatriz", "Carlos", "Diana", "Eduardo", "Fatima", "Gonzalo", "Helena", "Ignacio", "Julia",
        "Kevin", "Laura", "Manuel", "Nuria", "Oscar", "Patricia", "Ruben", "Sara", "Tomas", "Ursula",
        "Victor", "Wendy", "Xavier", "Yolanda", "Zacarias", "Ana", "Bruno", "Carmen", "David", "Elena"
    ];

    private static readonly string[] Apellidos =
    [
        "Garcia", "Martinez", "Lopez", "Sanchez", "Perez", "Gonzalez", "Rodriguez", "Fernandez", "Diaz", "Moreno",
        "Alvarez", "Romero", "Alonso", "Gutierrez", "Navarro", "Torres", "Dominguez", "Vazquez", "Ramos", "Gil"
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

    private static readonly string[] Ciudades =
    [
        "Madrid", "Barcelona", "Valencia", "Sevilla", "Bilbao", "Zaragoza", "Malaga", "Murcia", "Alicante", "Vigo"
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

        logger.LogInformation("Sembrando base de datos genérica de prueba (esto puede tardar unos segundos)…");

        var aleatorio = new Random(20260716);
        var contadorDni = 10_000_000;
        var contadorCif = 1_000_000;

        // --- Clientes ---
        const int numeroClientes = 200;
        var clientes = new List<Cliente>();
        for (var i = 0; i < numeroClientes; i++)
        {
            var cliente = new Cliente(
                razonSocial: $"{ElementoAleatorio(aleatorio, Ciudades)} {ElementoAleatorio(aleatorio, SectoresEmpresa)} {i + 1:D4}",
                cif: GenerarCifValido(contadorCif++),
                esCritico: aleatorio.Next(10) == 0,
                notas: null);
            clientes.Add(cliente);
        }
        dbContext.Clientes.AddRange(clientes);
        await dbContext.SaveChangesAsync(cancellationToken);

        // --- Empresas + EmpresaCliente (cada Empresa presta servicio a 1-3 Clientes) ---
        const int numeroEmpresas = 220;
        var empresas = new List<Empresa>();
        var empresasClientes = new List<EmpresaCliente>();
        for (var i = 0; i < numeroEmpresas; i++)
        {
            var empresa = new Empresa($"{ElementoAleatorio(aleatorio, SectoresEmpresa)} {ElementoAleatorio(aleatorio, Ciudades)} {i + 1:D4} {ElementoAleatorio(aleatorio, SufijosSociedad)}");
            empresas.Add(empresa);

            foreach (var cliente in ElementosAleatoriosUnicos(aleatorio, clientes, aleatorio.Next(1, 4)))
                empresasClientes.Add(new EmpresaCliente(empresa.Id, cliente.Id));
        }
        dbContext.Empresas.AddRange(empresas);
        dbContext.EmpresasClientes.AddRange(empresasClientes);
        await dbContext.SaveChangesAsync(cancellationToken);

        // --- Subcontratas + SubcontrataCliente + SubcontrataEmpresa ---
        const int numeroSubcontratas = 200;
        var subcontratas = new List<Subcontrata>();
        var subcontratasClientes = new List<SubcontrataCliente>();
        var subcontratasEmpresas = new List<SubcontrataEmpresa>();
        for (var i = 0; i < numeroSubcontratas; i++)
        {
            var subcontrata = new Subcontrata($"Subcontrata {ElementoAleatorio(aleatorio, SectoresEmpresa)} {i + 1:D4} {ElementoAleatorio(aleatorio, SufijosSociedad)}");
            subcontratas.Add(subcontrata);

            foreach (var cliente in ElementosAleatoriosUnicos(aleatorio, clientes, aleatorio.Next(1, 3)))
                subcontratasClientes.Add(new SubcontrataCliente(subcontrata.Id, cliente.Id));

            foreach (var empresa in ElementosAleatoriosUnicos(aleatorio, empresas, aleatorio.Next(1, 4)))
                subcontratasEmpresas.Add(new SubcontrataEmpresa(subcontrata.Id, empresa.Id));
        }
        dbContext.Subcontratas.AddRange(subcontratas);
        dbContext.SubcontratasClientes.AddRange(subcontratasClientes);
        dbContext.SubcontratasEmpresas.AddRange(subcontratasEmpresas);
        await dbContext.SaveChangesAsync(cancellationToken);

        // --- Centros (cada uno de un Cliente y una de sus Empresas asociadas) ---
        const int numeroCentros = 300;
        var centros = new List<Centro>();
        var empresasPorCliente = empresasClientes
            .GroupBy(ec => ec.ClienteId)
            .ToDictionary(g => g.Key, g => g.Select(ec => ec.EmpresaId).ToList());

        for (var i = 0; i < numeroCentros; i++)
        {
            var cliente = ElementoAleatorio(aleatorio, clientes);
            if (!empresasPorCliente.TryGetValue(cliente.Id, out var empresaIdsDelCliente) || empresaIdsDelCliente.Count == 0)
                continue;

            var empresaId = ElementoAleatorio(aleatorio, empresaIdsDelCliente);
            var centro = new Centro(
                clienteId: cliente.Id,
                empresaId: empresaId,
                nombre: $"{ElementoAleatorio(aleatorio, NombresCentro)} {ElementoAleatorio(aleatorio, Ciudades)} {i + 1:D4}",
                codigoCentro: $"CTR-{i + 1:D5}",
                direccion: $"Calle {ElementoAleatorio(aleatorio, Apellidos)} {aleatorio.Next(1, 200)}, {ElementoAleatorio(aleatorio, Ciudades)}",
                contacto: null,
                contratoVigenteHasta: null);
            centros.Add(centro);
        }
        dbContext.Centros.AddRange(centros);
        await dbContext.SaveChangesAsync(cancellationToken);

        // --- Trabajadores (~70% de Empresa, ~30% de Subcontrata) ---
        const int numeroTrabajadores = 500;
        var trabajadores = new List<Trabajador>();
        for (var i = 0; i < numeroTrabajadores; i++)
        {
            var nombre = ElementoAleatorio(aleatorio, NombresPila);
            var apellidos = $"{ElementoAleatorio(aleatorio, Apellidos)} {ElementoAleatorio(aleatorio, Apellidos)}";
            var dni = GenerarDniValido(contadorDni++);

            var trabajador = aleatorio.Next(100) < 70
                ? Trabajador.DeEmpresa(ElementoAleatorio(aleatorio, empresas).Id, nombre, apellidos, dni)
                : Trabajador.DeSubcontrata(ElementoAleatorio(aleatorio, subcontratas).Id, nombre, apellidos, dni);

            trabajadores.Add(trabajador);
        }
        dbContext.Trabajadores.AddRange(trabajadores);
        await dbContext.SaveChangesAsync(cancellationToken);

        // --- Asignaciones (cada Trabajador en 1-2 Centros) ---
        var asignaciones = new List<Asignacion>();
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var trabajador in trabajadores)
        {
            if (centros.Count == 0) break;

            foreach (var centro in ElementosAleatoriosUnicos(aleatorio, centros, aleatorio.Next(1, 3)))
            {
                var fechaAlta = hoy.AddDays(-aleatorio.Next(30, 900));
                asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, fechaAlta));
            }
        }
        dbContext.Asignaciones.AddRange(asignaciones);
        await dbContext.SaveChangesAsync(cancellationToken);

        // --- Documentos (1-3 por Trabajador, repartidos entre vigente/próximo/urgente/vencido) ---
        // El Id de TipoDocumentoSeedData.Datos es fijo para las filas del
        // tenant #1 (HasData de migración) — este seeder puede correr sellado
        // a un tenant distinto (ver DelegacionDemoSeeder), que tiene su
        // propia copia del catálogo con Ids nuevos. Se resuelve por Nombre
        // contra las filas reales del tenant ambiente en vez de asumir el Id
        // fijo, o los Documento generados aquí referenciarían un
        // TipoDocumentoId invisible bajo el filtro de tenant del ambiente
        // actual.
        var tiposDocumentoPorNombre = await dbContext.TiposDocumento
            .ToDictionaryAsync(t => t.Nombre, t => t.Id, cancellationToken);

        var documentos = new List<Documento>();
        foreach (var trabajador in trabajadores)
        {
            foreach (var tipoDocumento in ElementosAleatoriosUnicos(aleatorio, TipoDocumentoSeedData.Datos, aleatorio.Next(1, 4)))
            {
                var diasHastaVencimiento = aleatorio.Next(100) switch
                {
                    < 15 => -aleatorio.Next(1, 200),   // vencido
                    < 30 => aleatorio.Next(1, 15),     // urgente
                    < 50 => aleatorio.Next(15, 45),    // próximo
                    _ => aleatorio.Next(45, 500)        // vigente
                };

                var fechaVencimiento = tipoDocumento.AplicaVencimiento ? hoy.AddDays(diasHastaVencimiento) : (DateOnly?)null;
                var fechaEmision = fechaVencimiento?.AddMonths(-(tipoDocumento.VigenciaMeses ?? 12)) ?? hoy.AddDays(-aleatorio.Next(30, 400));
                if (fechaEmision > hoy) fechaEmision = hoy;

                documentos.Add(Documento.DeTrabajador(trabajador.Id, tiposDocumentoPorNombre[tipoDocumento.Nombre], fechaEmision, fechaVencimiento));
            }
        }
        dbContext.Documentos.AddRange(documentos);
        await dbContext.SaveChangesAsync(cancellationToken);

        // --- Usuarios de prueba: 3 por cada uno de los 6 roles ---
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
                usuariosPorRol[rol].Add(usuario);
            }
        }

        // --- Cartera de prueba: sin esto, los usuarios de prueba de los
        // roles con alcance acotado (GestorCae/CoordinadorCae/Cliente) no
        // verían ningún dato — ver IAlcanceDatosService (Fase 31). Los tres
        // GestorCae de prueba reportan al primer CoordinadorCae, se reparten
        // los primeros 30 Clientes sembrados como cartera, y cada usuario
        // Cliente de prueba queda vinculado a un Cliente real distinto.
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
            const int clientesParaCartera = 30;
            for (var i = 0; i < Math.Min(clientesParaCartera, clientes.Count); i++)
                clientes[i].AsignarEjecutivo(gestoresPrueba[i % gestoresPrueba.Count].Id);
        }

        var clientesPrueba = usuariosPorRol[Roles.Cliente];
        for (var i = 0; i < clientesPrueba.Count && i < clientes.Count; i++)
        {
            clientesPrueba[i].ClienteId = clientes[i].Id;
            await userManager.UpdateAsync(clientesPrueba[i]);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Datos de prueba sembrados: {Clientes} clientes, {Empresas} empresas, {Subcontratas} subcontratas, " +
            "{Centros} centros, {Trabajadores} trabajadores, {Documentos} documentos, {Usuarios} usuarios de prueba " +
            "(contraseña «{Contrasena}» para todos, email prueba.<rol><n>@caemanager.local).",
            clientes.Count, empresas.Count, subcontratas.Count, centros.Count, trabajadores.Count, documentos.Count,
            Roles.Todos.Count * 3, ContrasenaUsuariosPrueba);
    }

    private static string GenerarDniValido(int numero)
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
