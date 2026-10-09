using System.Security.Claims;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// La autoverificación de la siembra del piloto: mide, con las mismas consultas
/// que pintan las pantallas, los contadores de cada Tenant propietario
/// (<see cref="MedirAsync(IServiceScopeFactory, OpcionesPilotoOutbound, CancellationToken)"/>)
/// y los compara con los valores de la matriz (<see cref="Exigir"/>, pura).
///
/// <para>
/// <b>Con qué identidad mide.</b> Las consultas de pantalla necesitan un usuario:
/// Mi trabajo, Inicio, Centros, Empresas y Clientes empresariales se miden como la
/// Gestora CAE primera del piloto, y Visión de cartera —que la Gestora CAE no
/// puede abrir— como su Coordinadora CAE. La identidad la construye
/// <see cref="ComoCuentaDelPilotoAsync{T}"/>, privado, y solo para esas dos
/// cuentas: comprueba en la base que la cuenta pertenece al Tenant del Operador
/// CAE externo del piloto, que ese Tenant lleva el marcador de datos de demo y
/// que la cuenta tiene exactamente el rol esperado. Vive lo que dura una lectura,
/// fuera de toda petición, y se retira en un <c>finally</c>. No hay ninguna
/// entrada que construya una identidad para otra cuenta: es el único sitio de
/// <c>src/</c> que crea un contexto HTTP a mano, y un trinquete lo fija.
/// </para>
/// </summary>
public static class PilotoOutboundAutoverificacion
{
    public sealed record MedicionCentro(string Nombre, EstadoCentro Estado, int? Cumplimiento);

    /// <summary>Lo medido de un Tenant propietario. Los nombres dicen de qué pantalla sale cada contador.</summary>
    public sealed record MedicionTenant(
        string Clave, string Nombre,
        bool MiTrabajoPresente, bool MiTrabajoNoConsultado, bool MiTrabajoAlcanceCero,
        int MiTrabajoBloqueos, int MiTrabajoActuaciones, int MiTrabajoProximos, int MiTrabajoSeguimiento,
        int InicioCumplimiento, int InicioVencidos, int InicioUrgentes, int InicioProximos, int InicioSinConfirmar,
        int InicioCentrosBloqueados, int InicioTrabajadoresBloqueados, int InicioVisitasUrgentes,
        bool VisionCarteraPresente, int? VisionCarteraCumplimiento, bool VisionCarteraSinCartera, bool VisionCarteraSinDatos,
        IReadOnlyList<MedicionCentro> Centros, int? EmpresaCumplimiento,
        int ParesExigidos, int ParesFaltantes,
        int ClientesEmpresariales, int ClientesEmpresarialesSinContacto, int ClientesEmpresarialesConAlertas,
        int Documentos, int DocumentosSinPdf, int ContactosDeAgenda, int ContactosFueraDeLaReglaDeCorreo)
    {
        public int MiTrabajoFilas => MiTrabajoBloqueos + MiTrabajoActuaciones + MiTrabajoProximos + MiTrabajoSeguimiento;
    }

    public sealed record Informe(VarianteT4PilotoOutbound VarianteT4, IReadOnlyList<MedicionTenant> Tenants)
    {
        public MedicionTenant De(TenantPilotoOutbound tenant) => Tenants.Single(t => t.Clave == tenant.Clave);
    }

    /// <summary>Mide con las cuentas de la siembra local.</summary>
    public static Task<Informe> MedirAsync(
        IServiceScopeFactory fabricaDeAmbitos, OpcionesPilotoOutbound opciones, CancellationToken cancellationToken = default) =>
        MedirAsync(fabricaDeAmbitos, CuentasPilotoOutbound.Locales, opciones.Contactos, opciones.VarianteT4, cancellationToken);

    /// <summary>
    /// Mide con las cuentas indicadas, que tienen que ser la Gestora CAE primera y la
    /// Coordinadora CAE sembradas en el Tenant del Operador CAE externo del piloto
    /// (ver <see cref="ComoCuentaDelPilotoAsync{T}"/>). Solo lectura.
    /// </summary>
    internal static async Task<Informe> MedirAsync(
        IServiceScopeFactory fabricaDeAmbitos, CuentasPilotoOutbound cuentas, ContactosPilotoOutbound contactos,
        VarianteT4PilotoOutbound varianteT4, CancellationToken cancellationToken)
    {
        var comoGestora = await ComoCuentaDelPilotoAsync(
            fabricaDeAmbitos, cuentas.GestoraPrimera, Roles.GestorCae,
            servicios => MedirComoGestoraAsync(servicios, contactos, cancellationToken), cancellationToken);

        var visionCartera = await ComoCuentaDelPilotoAsync(
            fabricaDeAmbitos, cuentas.Coordinadora, Roles.CoordinadorCae,
            async servicios => (await servicios.GetRequiredService<ISender>().Send(new ObtenerKpisGlobalesQuery(), cancellationToken))
                .ClientesConMasRiesgo.ToDictionary(c => c.Nombre),
            cancellationToken);

        return new Informe(varianteT4,
        [
            .. comoGestora.Select(m => m with
            {
                VisionCarteraPresente = visionCartera.ContainsKey(m.Nombre),
                VisionCarteraCumplimiento = visionCartera.GetValueOrDefault(m.Nombre)?.TasaCumplimientoDocumental,
                VisionCarteraSinCartera = visionCartera.GetValueOrDefault(m.Nombre)?.SinCarteraAsignada ?? false,
                VisionCarteraSinDatos = visionCartera.GetValueOrDefault(m.Nombre)?.SinDatos ?? false
            })
        ]);
    }

    /// <summary>
    /// Todas las discrepancias entre lo medido y la matriz, cada una con su Tenant
    /// y su contador. Vacía si la siembra está como la matriz dice.
    /// </summary>
    public static IReadOnlyList<string> Discrepancias(Informe informe)
    {
        var d = new List<string>();

        foreach (var tenant in CatalogoPilotoOutbound.Tenants)
        {
            if (informe.Tenants.SingleOrDefault(t => t.Clave == tenant.Clave) is not { } m)
            {
                d.Add($"{tenant.Clave} «{tenant.Nombre}»: el Tenant no existe o no se pudo medir.");
                continue;
            }

            void Exige<T>(string contador, T medido, T esperado)
            {
                if (!EqualityComparer<T>.Default.Equals(medido, esperado))
                    d.Add($"{tenant.Clave} «{tenant.Nombre}» · {contador}: medido {Texto(medido)}, esperado {Texto(esperado)}.");
            }

            // Comunes a los seis: la cola se pudo consultar, cada documento abre como PDF y «Pedir» tiene a quién escribir.
            Exige("Mi trabajo · Tenant presente en la cartera de la Gestora CAE", m.MiTrabajoPresente, true);
            Exige("Mi trabajo · cola que no se pudo consultar", m.MiTrabajoNoConsultado, false);
            Exige("Documentos · sin PDF que se abra", m.DocumentosSinPdf, 0);
            Exige("Agenda · contactos fuera de la regla de correo", m.ContactosFueraDeLaReglaDeCorreo, 0);
            Exige("Clientes empresariales · sin contacto en la agenda", m.ClientesEmpresarialesSinContacto, 0);
            Exige("Agenda · tiene contactos", m.ContactosDeAgenda > 0, true);

            if (CatalogoPilotoOutbound.Esperado(tenant, informe.VarianteT4) is not { } e)
                continue;

            // «Cero filas» solo vale con alcance: un Tenant sin cartera también daría cero.
            Exige("Mi trabajo · alcance cero", m.MiTrabajoAlcanceCero, false);
            Exige("Mi trabajo · filas", m.MiTrabajoFilas, e.FilasMiTrabajo);
            if (e.CumplimientoInicio is { } inicio)
                Exige("Inicio · % de cumplimiento", m.InicioCumplimiento, inicio);
            Exige("Visión de cartera · Tenant presente para la Coordinadora CAE", m.VisionCarteraPresente, true);
            // Con cualquiera de las dos, la fila pinta «Sin cartera» o «Sin datos» en vez del porcentaje.
            Exige("Visión de cartera · fila sin cartera asignada", m.VisionCarteraSinCartera, false);
            Exige("Visión de cartera · fila sin datos", m.VisionCarteraSinDatos, false);
            if (e.CumplimientoVisionCartera is { } vision)
                Exige("Visión de cartera · % de cumplimiento", m.VisionCarteraCumplimiento, vision);
            Exige("Empresas · % de cumplimiento de la Empresa propia", m.EmpresaCumplimiento, e.CumplimientoEmpresa);
            Exige("Centros · número de Centros", m.Centros.Count, e.Centros.Count);
            foreach (var centroEsperado in e.Centros)
            {
                var centro = m.Centros.SingleOrDefault(c => c.Nombre == centroEsperado.Centro);
                Exige($"Centros · «{centroEsperado.Centro}» · estado", centro?.Estado, centroEsperado.Estado);
                Exige($"Centros · «{centroEsperado.Centro}» · % de cumplimiento", centro?.Cumplimiento, centroEsperado.Cumplimiento);
            }

            Exige("Pares exigidos", m.ParesExigidos, e.ParesExigidos);
            Exige("Pares exigidos · Faltante", m.ParesFaltantes, e.ParesFaltantes);
            Exige("Documentos · total", m.Documentos, e.Documentos);

            if (!e.TodoAlDia) continue;

            Exige("Mi trabajo · bloqueos", m.MiTrabajoBloqueos, 0);
            Exige("Mi trabajo · actuaciones", m.MiTrabajoActuaciones, 0);
            Exige("Mi trabajo · próximos", m.MiTrabajoProximos, 0);
            Exige("Mi trabajo · seguimiento", m.MiTrabajoSeguimiento, 0);
            Exige("Inicio · documentos vencidos", m.InicioVencidos, 0);
            Exige("Inicio · documentos urgentes", m.InicioUrgentes, 0);
            Exige("Inicio · documentos próximos", m.InicioProximos, 0);
            Exige("Inicio · documentos sin confirmar", m.InicioSinConfirmar, 0);
            Exige("Inicio · Centros bloqueados", m.InicioCentrosBloqueados, 0);
            Exige("Inicio · Trabajadores bloqueados", m.InicioTrabajadoresBloqueados, 0);
            Exige("Inicio · Visitas urgentes", m.InicioVisitasUrgentes, 0);
            Exige("Clientes empresariales · con alguna alerta documental", m.ClientesEmpresarialesConAlertas, 0);
        }

        // Control positivo de «cero filas»: en la MISMA lectura de Mi trabajo, otro Tenant sí trae filas.
        if (informe.Tenants.All(t => t.MiTrabajoFilas == 0))
            d.Add("Mi trabajo · control positivo: ningún Tenant del piloto trae filas, así que un cero no demuestra nada.");

        return d;
    }

    /// <summary>
    /// Las divergencias declaradas del catálogo, con lo que se ha medido: no hacen
    /// fallar, pero quien prepara la demostración tiene que leerlas.
    /// </summary>
    public static IReadOnlyList<string> Advertencias(Informe informe) =>
    [
        .. from tenant in CatalogoPilotoOutbound.Tenants
           let esperado = CatalogoPilotoOutbound.Esperado(tenant, informe.VarianteT4)
           where esperado?.Divergencia is not null
           let m = informe.Tenants.SingleOrDefault(t => t.Clave == tenant.Clave)
           where m is not null
           select $"{tenant.Clave} «{tenant.Nombre}»: {esperado.Divergencia} Medido: Inicio {m.InicioCumplimiento} %, " +
                  $"Visión de cartera {Texto(m.VisionCarteraCumplimiento)} %."
    ];

    /// <summary>Lanza con TODAS las discrepancias si lo medido no es lo que la matriz declara.</summary>
    public static void Exigir(Informe informe)
    {
        var discrepancias = Discrepancias(informe);
        if (discrepancias.Count > 0)
            throw new InvalidOperationException(
                $"La siembra del piloto Outbound no da los resultados de su matriz ({discrepancias.Count} discrepancias):" +
                Environment.NewLine + string.Join(Environment.NewLine, discrepancias.Select(x => " - " + x)));
    }

    private static string Texto<T>(T valor) => valor is null ? "(nada)" : valor.ToString()!;

    private static async Task<IReadOnlyList<MedicionTenant>> MedirComoGestoraAsync(
        IServiceProvider servicios, ContactosPilotoOutbound contactos, CancellationToken cancellationToken)
    {
        var sender = servicios.GetRequiredService<ISender>();
        var dbContext = servicios.GetRequiredService<CaeManagerDbContext>();
        var calculo = servicios.GetRequiredService<ICalculoEstadoCentroService>();
        var almacen = servicios.GetRequiredService<IFileStorageService>();

        var nombres = CatalogoPilotoOutbound.Tenants.Select(t => t.Nombre).ToList();
        var idPorNombre = await dbContext.Tenants.AsNoTracking()
            .Where(t => nombres.Contains(t.Nombre)).ToDictionaryAsync(t => t.Nombre, t => t.Id, cancellationToken);

        // Una sola lectura de Mi trabajo para toda la cartera, como la pantalla.
        var miTrabajo = await sender.Send(new ObtenerMiTrabajoAgregadoQuery(), cancellationToken);

        var mediciones = new List<MedicionTenant>();
        foreach (var tenant in CatalogoPilotoOutbound.Tenants)
        {
            if (!idPorNombre.TryGetValue(tenant.Nombre, out var tenantId)) continue;

            var cola = miTrabajo.Tenants.SingleOrDefault(t => t.TenantId == tenantId);
            var noConsultado = miTrabajo.NoConsultados.Any(t => t.TenantId == tenantId);

            using (AmbitoTenantExplicito.Establecer(tenantId))
            {
                var kpis = await sender.Send(new ObtenerKpisDashboardQuery(), cancellationToken);
                var centros = (await sender.Send(new ObtenerCentrosQuery(null, null, TamanoPagina: 1000), cancellationToken)).Elementos;
                var empresas = (await sender.Send(new ObtenerEmpresasQuery(null, TamanoPagina: 1000), cancellationToken)).Elementos;
                var clientes = (await sender.Send(new ObtenerClientesQuery(null, null, TamanoPagina: 1000), cancellationToken)).Elementos;
                var pares = await calculo.ObtenerParesExigidosAsync([.. centros.Select(c => c.Id)], cancellationToken);

                var claves = await dbContext.Documentos.AsNoTracking().Select(d => d.ArchivoUrl).ToListAsync(cancellationToken);
                var sinPdf = 0;
                foreach (var clave in claves)
                    if (clave is null || !await EsPdfAsync(almacen, clave, cancellationToken))
                        sinPdf++;

                var correos = await dbContext.ContactosAgenda.AsNoTracking().Select(c => c.Email).ToListAsync(cancellationToken);

                mediciones.Add(new MedicionTenant(
                    tenant.Clave, tenant.Nombre,
                    MiTrabajoPresente: cola is not null || noConsultado,
                    MiTrabajoNoConsultado: noConsultado,
                    MiTrabajoAlcanceCero: cola?.AlcanceCero ?? false,
                    MiTrabajoBloqueos: cola?.Resumen.Bloqueos ?? 0,
                    MiTrabajoActuaciones: cola?.Resumen.Actuaciones ?? 0,
                    MiTrabajoProximos: cola?.Resumen.Proximos ?? 0,
                    MiTrabajoSeguimiento: cola?.Resumen.Seguimiento ?? 0,
                    InicioCumplimiento: kpis.TasaCumplimientoDocumental,
                    InicioVencidos: kpis.DocumentosVencidos,
                    InicioUrgentes: kpis.DocumentosUrgentes,
                    InicioProximos: kpis.DocumentosProximos,
                    InicioSinConfirmar: kpis.DocumentosSinConfirmar,
                    InicioCentrosBloqueados: kpis.CentrosBloqueados,
                    InicioTrabajadoresBloqueados: kpis.TrabajadoresBloqueados,
                    InicioVisitasUrgentes: kpis.VisitasUrgentes,
                    VisionCarteraPresente: false,
                    VisionCarteraCumplimiento: null,
                    VisionCarteraSinCartera: false,
                    VisionCarteraSinDatos: false,
                    Centros: [.. centros.Select(c => new MedicionCentro(c.Nombre, c.Estado, c.CumplimientoPorcentaje))],
                    EmpresaCumplimiento: empresas.SingleOrDefault(e => e.RazonSocial == tenant.Nombre)?.CumplimientoPorcentaje,
                    ParesExigidos: pares.Count,
                    ParesFaltantes: pares.Count(p => p.Estado == EstadoDocumento.Faltante),
                    ClientesEmpresariales: clientes.Count,
                    ClientesEmpresarialesSinContacto: clientes.Count(c => c.SinContactoEnAgenda),
                    ClientesEmpresarialesConAlertas: clientes.Count(c => c.EstadoDocumentalPeor is not null || c.EstadoDocumentalCantidad != 0),
                    Documentos: claves.Count,
                    DocumentosSinPdf: sinPdf,
                    ContactosDeAgenda: correos.Count,
                    ContactosFueraDeLaReglaDeCorreo: correos.Count(c => !contactos.Cumple(c))));
            }
        }

        return mediciones;
    }

    private static async Task<bool> EsPdfAsync(IFileStorageService almacen, string clave, CancellationToken cancellationToken)
    {
        try
        {
            await using var flujo = await almacen.AbrirAsync(clave, cancellationToken);
            var cabecera = new byte[5];
            return await flujo.ReadAtLeastAsync(cabecera, cabecera.Length, throwOnEndOfStream: false, cancellationToken) == cabecera.Length
                   && cabecera.AsSpan().SequenceEqual("%PDF-"u8);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Ejecuta una lectura con la identidad de una cuenta sembrada del piloto, en un
    /// ámbito de servicios nuevo y fuera de toda petición.
    ///
    /// <para>
    /// Antes de construir nada comprueba, leyendo la base, que la cuenta es del
    /// Tenant del Operador CAE externo del piloto (localizado por su nombre del
    /// catálogo), que ese Tenant lleva el marcador de datos de demo y puede actuar
    /// como Operador CAE externo, y que la cuenta tiene exactamente <paramref name="rolExigido"/>.
    /// La identidad es la que emite la fábrica de claims de la aplicación para esa
    /// cuenta —la misma que tendría tras iniciar sesión—, sin añadirle nada. Se
    /// publica en el accesor de contexto HTTP solo mientras corre
    /// <paramref name="lectura"/> y se retira en el <c>finally</c>.
    /// </para>
    /// </summary>
    private static async Task<T> ComoCuentaDelPilotoAsync<T>(
        IServiceScopeFactory fabricaDeAmbitos, string email, string rolExigido,
        Func<IServiceProvider, Task<T>> lectura, CancellationToken cancellationToken)
    {
        await using var ambito = fabricaDeAmbitos.CreateAsyncScope();
        var servicios = ambito.ServiceProvider;

        var accesor = servicios.GetRequiredService<IHttpContextAccessor>();
        if (accesor.HttpContext is not null)
            throw new InvalidOperationException("La autoverificación del piloto solo corre fuera de una petición HTTP.");

        var dbContext = servicios.GetRequiredService<CaeManagerDbContext>();
        var operador = await dbContext.Tenants.AsNoTracking()
            .Where(t => t.Nombre == CatalogoPilotoOutbound.NombreTenantOperador)
            .Select(t => new { t.Id, t.DatosDemoCompletadosEnUtc, t.PuedeActuarComoOperadorCaeExterno })
            .SingleOrDefaultAsync(cancellationToken);

        if (operador is null || !operador.PuedeActuarComoOperadorCaeExterno || operador.DatosDemoCompletadosEnUtc is null)
            throw new InvalidOperationException(
                "La autoverificación del piloto solo mide con cuentas del Tenant del Operador CAE externo del piloto, y ese " +
                "Tenant no existe, no es un Operador CAE externo o no lleva el marcador de datos de demo.");

        ClaimsPrincipal identidad;
        using (AmbitoTenantExplicito.Establecer(operador.Id))
        {
            var userManager = servicios.GetRequiredService<UserManager<ApplicationUser>>();
            var cuenta = await userManager.FindByEmailAsync(email);
            if (cuenta is null || cuenta.TenantId != operador.Id)
                throw new InvalidOperationException(
                    "La autoverificación del piloto solo mide con las cuentas sembradas en el Tenant del Operador CAE externo " +
                    $"del piloto, y la cuenta de {rolExigido} indicada no es de ese Tenant.");

            var roles = await userManager.GetRolesAsync(cuenta);
            if (roles.Count != 1 || roles[0] != rolExigido)
                throw new InvalidOperationException(
                    $"La cuenta del piloto con la que se iba a medir no tiene exactamente el rol {rolExigido}: no se construye su identidad.");

            identidad = await servicios.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>().CreateAsync(cuenta);
        }

        accesor.HttpContext = new DefaultHttpContext { User = identidad, RequestServices = servicios };
        try
        {
            var resultado = await lectura(servicios);

            if (dbContext.ChangeTracker.HasChanges())
                throw new InvalidOperationException("La autoverificación del piloto es de solo lectura y ha dejado cambios sin guardar.");

            return resultado;
        }
        finally
        {
            accesor.HttpContext = null;
        }
    }
}
