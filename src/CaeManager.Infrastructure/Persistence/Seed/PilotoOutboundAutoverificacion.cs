using System.IO.Compression;
using System.Security.Claims;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador;
using CaeManager.Application.Visitas.GestionPorCorreo;
using CaeManager.Application.Visitas.PaqueteDocumental;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Gestiones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        int Documentos, int DocumentosSinPdf, int ContactosDeAgenda, int ContactosFueraDeLaReglaDeCorreo,
        MedicionGrande? Grande = null)
    {
        public int MiTrabajoFilas => MiTrabajoBloqueos + MiTrabajoActuaciones + MiTrabajoProximos + MiTrabajoSeguimiento;
    }

    /// <summary>
    /// Lo que solo se mide del Tenant grande: su estructura, cuántas veces aparece
    /// cada caso de estado de la matriz (<see cref="CasosDeEstadoPilotoOutbound"/>) y
    /// el paquete documental de la primera Visita a un Centro que se gestiona por correo.
    /// </summary>
    public sealed record MedicionGrande(
        int TrabajadoresPropios, int TrabajadoresDeSubcontrata, int Subcontratas,
        IReadOnlyDictionary<string, int> CasosDeEstado, int VisitasACentrosPorCorreo, MedicionPaquete? Paquete);

    /// <summary>
    /// El ZIP de una Visita, construido con el servicio que usa la pantalla, contra lo
    /// que su Centro exige a la Empresa propia y a cada Trabajador que acude.
    /// </summary>
    /// <param name="DiasDesdeLaDemostracion">Días entre la demostración y el inicio de la Visita: no depende del día en que se mide.</param>
    /// <param name="DiasDesdeHoy">Días entre hoy y el inicio de la Visita: SÍ depende del día en que se mide.</param>
    /// <param name="PdfDeTrabajador">Ficheros del ZIP, en la carpeta de Trabajadores, que abren como PDF.</param>
    /// <param name="Faltan">Lo exigido que no está en el ZIP, por su nombre.</param>
    /// <param name="Sobran">Lo que está en el ZIP y el Centro no exige, por su nombre.</param>
    public sealed record MedicionPaquete(
        string Centro, string CorreosDelCanal, int CorreosDelCanalFueraDeLaRegla,
        int DiasDesdeLaDemostracion, int DiasDesdeHoy, int Trabajadores,
        int ExigidosDeTrabajador, int ExigidosDeEmpresa, int PdfDeTrabajador, int PdfDeEmpresa, int FicherosEnElZip,
        IReadOnlyList<string> Faltan, IReadOnlyList<string> Sobran);

    public sealed record Informe(IReadOnlyList<MedicionTenant> Tenants)
    {
        public MedicionTenant De(TenantPilotoOutbound tenant) => Tenants.Single(t => t.Clave == tenant.Clave);
    }

    /// <summary>Mide con las cuentas de la siembra local.</summary>
    public static Task<Informe> MedirAsync(
        IServiceScopeFactory fabricaDeAmbitos, OpcionesPilotoOutbound opciones, CancellationToken cancellationToken = default) =>
        MedirAsync(fabricaDeAmbitos, CuentasPilotoOutbound.Locales, opciones, cancellationToken);

    /// <summary>
    /// Mide con las cuentas indicadas, que tienen que ser la Gestora CAE primera y la
    /// Coordinadora CAE sembradas en el Tenant del Operador CAE externo del piloto
    /// (ver <see cref="ComoCuentaDelPilotoAsync{T}"/>). Solo lectura.
    /// </summary>
    internal static async Task<Informe> MedirAsync(
        IServiceScopeFactory fabricaDeAmbitos, CuentasPilotoOutbound cuentas, OpcionesPilotoOutbound opciones,
        CancellationToken cancellationToken)
    {
        var comoGestora = await ComoCuentaDelPilotoAsync(
            fabricaDeAmbitos, cuentas.GestoraPrimera, Roles.GestorCae,
            servicios => MedirComoGestoraAsync(servicios, opciones, cancellationToken), cancellationToken);

        var visionCartera = await ComoCuentaDelPilotoAsync(
            fabricaDeAmbitos, cuentas.Coordinadora, Roles.CoordinadorCae,
            async servicios => (await servicios.GetRequiredService<ISender>().Send(new ObtenerKpisGlobalesQuery(), cancellationToken))
                .ClientesConMasRiesgo.ToDictionary(c => c.Nombre),
            cancellationToken);

        return new Informe(
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

            void Discrepa(string contador, string medido, string esperado) =>
                d.Add($"{tenant.Clave} «{tenant.Nombre}» · {contador}: medido {medido}, esperado {esperado}.");

            void Exige<T>(string contador, T medido, T esperado)
            {
                if (!EqualityComparer<T>.Default.Equals(medido, esperado))
                    Discrepa(contador, Texto(medido), Texto(esperado));
            }

            // Comunes a los seis: la cola se pudo consultar, cada documento abre como PDF y «Pedir» tiene a quién escribir.
            Exige("Mi trabajo · Tenant presente en la cartera de la Gestora CAE", m.MiTrabajoPresente, true);
            Exige("Mi trabajo · cola que no se pudo consultar", m.MiTrabajoNoConsultado, false);
            Exige("Documentos · sin PDF que se abra", m.DocumentosSinPdf, 0);
            Exige("Agenda · contactos fuera de la regla de correo", m.ContactosFueraDeLaReglaDeCorreo, 0);
            Exige("Clientes empresariales · sin contacto en la agenda", m.ClientesEmpresarialesSinContacto, 0);
            Exige("Agenda · tiene contactos", m.ContactosDeAgenda > 0, true);

            if (CatalogoPilotoOutbound.EsperadoGrande(tenant) is { } grande)
                ExigirGrande(m, grande, Discrepa);

            if (CatalogoPilotoOutbound.Esperado(tenant) is not { } e)
                continue;

            // «Cero filas» solo vale con alcance: un Tenant sin cartera también daría cero.
            Exige("Mi trabajo · alcance cero", m.MiTrabajoAlcanceCero, false);
            Exige("Mi trabajo · filas", m.MiTrabajoFilas, e.FilasMiTrabajo);
            // Inicio y Visión de cartera se exigen siempre, también donde divergen de Empresas: la
            // divergencia declarada tiene su número, y si la pantalla cambia de regla esto lo dice.
            Exige("Inicio · % de cumplimiento", m.InicioCumplimiento, e.CumplimientoInicio);
            Exige("Inicio · Trabajadores bloqueados", m.InicioTrabajadoresBloqueados, e.TrabajadoresBloqueados);
            Exige("Visión de cartera · Tenant presente para la Coordinadora CAE", m.VisionCarteraPresente, true);
            // Con cualquiera de las dos, la fila pinta «Sin cartera» o «Sin datos» en vez del porcentaje.
            Exige("Visión de cartera · fila sin cartera asignada", m.VisionCarteraSinCartera, false);
            Exige("Visión de cartera · fila sin datos", m.VisionCarteraSinDatos, false);
            Exige("Visión de cartera · % de cumplimiento", m.VisionCarteraCumplimiento, e.CumplimientoVisionCartera);
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
            Exige("Inicio · Visitas urgentes", m.InicioVisitasUrgentes, 0);
            Exige("Clientes empresariales · con alguna alerta documental", m.ClientesEmpresarialesConAlertas, 0);
        }

        // Control positivo de «cero filas»: en la MISMA lectura de Mi trabajo, otro Tenant sí trae filas.
        if (informe.Tenants.All(t => t.MiTrabajoFilas == 0))
            d.Add("Mi trabajo · control positivo: ningún Tenant del piloto trae filas, así que un cero no demuestra nada.");

        return d;
    }

    /// <summary>
    /// Lo que se exige del Tenant grande. La estructura, con cifras exactas; los
    /// porcentajes y el volumen de Mi trabajo, por intervalo, porque su diseño fija
    /// una proporción y no un número; cada caso de estado de la matriz, al menos una
    /// vez; y el ZIP de la Visita por correo, fichero a fichero contra lo que su
    /// Centro exige.
    ///
    /// <para>
    /// Una sola comprobación depende del día en que se mide: que Inicio cuente la
    /// Visita por correo entre las urgentes solo se exige cuando ya está a dos días
    /// o menos. Antes, esa Visita todavía no es urgente y no se exige nada.
    /// </para>
    /// </summary>
    private static void ExigirGrande(MedicionTenant m, EsperadoGrandePilotoOutbound e, Action<string, string, string> discrepa)
    {
        void Exacto(string contador, int? medido, int esperado)
        {
            if (medido != esperado) discrepa(contador, Texto(medido), Texto(esperado));
        }

        void Dentro(string contador, int? medido, IntervaloPilotoOutbound intervalo)
        {
            if (!intervalo.Contiene(medido)) discrepa(contador, Texto(medido), intervalo.ToString());
        }

        void Cierto(string contador, bool medido)
        {
            if (!medido) discrepa(contador, "no", "sí");
        }

        Cierto("Mi trabajo · la Gestora CAE tiene alcance en el Tenant", !m.MiTrabajoAlcanceCero);
        Dentro("Mi trabajo · filas", m.MiTrabajoFilas, e.FilasMiTrabajo);
        Dentro("Inicio · % de cumplimiento", m.InicioCumplimiento, e.CumplimientoInicioYVisionDeCartera);
        Cierto("Visión de cartera · Tenant presente para la Coordinadora CAE", m.VisionCarteraPresente);
        Cierto("Visión de cartera · la fila pinta un porcentaje", m is { VisionCarteraSinCartera: false, VisionCarteraSinDatos: false });
        Dentro("Visión de cartera · % de cumplimiento", m.VisionCarteraCumplimiento, e.CumplimientoInicioYVisionDeCartera);
        Dentro("Empresas · % de cumplimiento de la Empresa propia", m.EmpresaCumplimiento, e.CumplimientoEmpresa);
        Exacto("Centros · número de Centros", m.Centros.Count, e.Centros);
        Exacto("Clientes empresariales · número", m.ClientesEmpresariales, e.ClientesEmpresariales);
        Exacto("Pares exigidos", m.ParesExigidos, e.ParesExigidos);
        Exacto("Pares exigidos · Faltante", m.ParesFaltantes, e.ParesFaltantes);
        Exacto("Documentos · total", m.Documentos, e.Documentos);

        if (m.Grande is not { } g)
        {
            discrepa("Tenant grande · estructura, casos de estado y paquete de Visita", "sin medir", "medidos");
            return;
        }

        Exacto("Trabajadores · propios", g.TrabajadoresPropios, e.TrabajadoresPropios);
        Exacto("Trabajadores · de subcontrata", g.TrabajadoresDeSubcontrata, e.TrabajadoresDeSubcontrata);
        Exacto("Subcontratas", g.Subcontratas, e.Subcontratas);

        foreach (var caso in e.CasosDeEstado)
            if (g.CasosDeEstado.GetValueOrDefault(caso) < 1)
                discrepa($"Caso de estado · {caso}", Texto(g.CasosDeEstado.GetValueOrDefault(caso)), "al menos 1");

        if (g.Paquete is not { } p)
        {
            discrepa("Visita por correo · Visitas a un Centro cuyo canal principal es un correo", Texto(g.VisitasACentrosPorCorreo), "al menos 1");
            return;
        }

        Exacto("Visita por correo · direcciones del canal fuera de la regla de correo", p.CorreosDelCanalFueraDeLaRegla, 0);
        Exacto("Visita por correo · días desde la demostración hasta la Visita", p.DiasDesdeLaDemostracion, e.DiasHastaLaVisita);
        Exacto("Visita por correo · Trabajadores que acuden", p.Trabajadores, e.TrabajadoresDeLaVisita);
        Exacto("Paquete de la Visita · documentos de Trabajador que exige el Centro", p.ExigidosDeTrabajador, e.ExigidosDeTrabajadorEnElPaquete);
        Exacto("Paquete de la Visita · documentos de Empresa que exige el Centro", p.ExigidosDeEmpresa, e.ExigidosDeEmpresaEnElPaquete);
        Exacto("Paquete de la Visita · PDF de Trabajador en el ZIP", p.PdfDeTrabajador, p.ExigidosDeTrabajador);
        Exacto("Paquete de la Visita · PDF de Empresa en el ZIP", p.PdfDeEmpresa, p.ExigidosDeEmpresa);
        Exacto("Paquete de la Visita · ficheros en el ZIP", p.FicherosEnElZip, p.ExigidosDeTrabajador + p.ExigidosDeEmpresa);
        foreach (var falta in p.Faltan)
            discrepa($"Paquete de la Visita · exigido por el Centro: {falta}", "no está en el ZIP", "en el ZIP");
        foreach (var sobra in p.Sobran)
            discrepa($"Paquete de la Visita · no exigido por el Centro: {sobra}", "está en el ZIP", "fuera del ZIP");

        if (p.DiasDesdeHoy is >= 0 and <= 2 && m.InicioVisitasUrgentes < 1)
            discrepa("Inicio · Visitas urgentes (la Visita por correo está a dos días o menos)", Texto(m.InicioVisitasUrgentes), "al menos 1");
    }

    /// <summary>
    /// Las divergencias declaradas del catálogo, con lo que se ha medido: no hacen
    /// fallar, pero quien prepara la demostración tiene que leerlas. Hay una por
    /// cada Tenant cuyo valor esperado de Inicio o de Visión de cartera no coincide
    /// con el de Empresas, y solo mientras no coincida: el día que el catálogo los
    /// iguale, la advertencia desaparece sin tocar nada aquí.
    /// </summary>
    public static IReadOnlyList<string> Advertencias(Informe informe) =>
    [
        .. from tenant in CatalogoPilotoOutbound.Tenants
           let esperado = CatalogoPilotoOutbound.Esperado(tenant)
           where esperado is not null && esperado.InicioOVisionDeCarteraDivergenDeEmpresa
           let m = informe.Tenants.SingleOrDefault(t => t.Clave == tenant.Clave)
           where m is not null
           select $"{tenant.Clave} «{tenant.Nombre}»: " +
                  $"{esperado.Divergencia ?? "Inicio y Visión de cartera no dan la cifra de Empresas."} Medido: Inicio " +
                  $"{m.InicioCumplimiento} %, Visión de cartera {Texto(m.VisionCarteraCumplimiento)} %, Empresas {Texto(m.EmpresaCumplimiento)} %."
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

    /// <summary>
    /// El paso del arranque local tras la siembra: mide con las cuentas locales y decide
    /// según <paramref name="escribio"/>.
    /// </summary>
    public static Task MedirYExigirOAvisarAsync(
        IServiceScopeFactory fabricaDeAmbitos, OpcionesPilotoOutbound opciones, bool escribio, ILogger logger,
        CancellationToken cancellationToken = default) =>
        MedirYExigirOAvisarAsync(fabricaDeAmbitos, CuentasPilotoOutbound.Locales, opciones, escribio, logger, cancellationToken);

    /// <summary>
    /// Mide y, según la ejecución haya escrito o no, exige o avisa.
    ///
    /// <para>
    /// <b>Si acaba de escribir</b>, lo medido tiene que ser la matriz: una discrepancia lanza
    /// (<see cref="Exigir"/>) y no poder medir también, porque una siembra recién hecha que no
    /// se deja medir está mal.
    /// </para>
    ///
    /// <para>
    /// <b>Si no escribió</b> (un re-arranque con el lote ya sembrado), nada de esto lanza. Tras
    /// un ensayo los datos cambian a propósito —se descarta un documento, se cambia el rol de
    /// una cuenta, se borra un Centro—, y eso puede dar discrepancias o impedir la medición
    /// entera (la cuenta con la que se mide ya no tiene su rol, falta la fila que una lectura
    /// espera única). Las dos cosas quedan como advertencias en el registro y el arranque sigue.
    /// </para>
    /// </summary>
    internal static async Task MedirYExigirOAvisarAsync(
        IServiceScopeFactory fabricaDeAmbitos, CuentasPilotoOutbound cuentas, OpcionesPilotoOutbound opciones, bool escribio,
        ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var informe = await MedirAsync(fabricaDeAmbitos, cuentas, opciones, cancellationToken);

            foreach (var advertencia in Advertencias(informe))
                logger.LogWarning("Piloto Outbound, divergencia declarada: {Advertencia}", advertencia);

            if (escribio)
            {
                Exigir(informe);
                return;
            }

            foreach (var discrepancia in Discrepancias(informe))
                logger.LogWarning("Piloto Outbound, los datos ya no son los de la matriz: {Discrepancia}", discrepancia);
        }
        // Solo en el re-arranque, y solo lo que no sea una cancelación: con el lote recién escrito, todo lanza.
        catch (Exception ex) when (!escribio && ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "Piloto Outbound, no se ha podido medir el lote ya sembrado y el arranque sigue sin esa comprobación: {Motivo}",
                ex.Message);
        }
    }

    private static string Texto<T>(T valor) => valor is null ? "(nada)" : valor.ToString()!;

    private static async Task<IReadOnlyList<MedicionTenant>> MedirComoGestoraAsync(
        IServiceProvider servicios, OpcionesPilotoOutbound opciones, CancellationToken cancellationToken)
    {
        var contactos = opciones.Contactos;
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

                var grande = tenant.Escenario == EscenarioPilotoOutbound.Grande
                    ? await MedirGrandeAsync(servicios, dbContext, opciones, kpis, pares, [.. centros.Select(c => c.Id)], cancellationToken)
                    : null;

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
                    ContactosFueraDeLaReglaDeCorreo: correos.Count(c => !contactos.Cumple(c)),
                    Grande: grande));
            }
        }

        return mediciones;
    }

    /// <summary>
    /// Lo que solo se mide del Tenant grande, dentro del ámbito de Tenant que ya abrió
    /// quien llama y con la identidad de la Gestora CAE. Cada caso de estado se busca
    /// con la consulta o el servicio de la pantalla que lo enseña cuando da el dato
    /// (Inicio, la documentación por Centro de un Trabajador, la evaluación de acceso,
    /// el cálculo de pares exigidos, el paquete de la Visita) y, cuando no hay una
    /// consulta que lo cuente, leyendo las filas. Solo lectura.
    /// </summary>
    private static async Task<MedicionGrande> MedirGrandeAsync(
        IServiceProvider servicios, CaeManagerDbContext dbContext, OpcionesPilotoOutbound opciones, KpisDashboardDto kpis,
        IReadOnlyList<ParDocumentalExigido> pares, IReadOnlyCollection<Guid> centroIds, CancellationToken cancellationToken)
    {
        var hoy = DiaDeNegocio.Hoy();
        var d = opciones.FechaDemostracion;

        var trabajadores = await dbContext.Trabajadores.AsNoTracking()
            .Select(t => new { t.Id, t.Nombre, t.Apellidos, DeSubcontrata = t.SubcontrataId != null }).ToListAsync(cancellationToken);
        var asignaciones = await dbContext.Asignaciones.AsNoTracking()
            .Select(a => new { a.TrabajadorId, a.CentroId, Activa = a.FechaBaja == null }).ToListAsync(cancellationToken);
        var documentos = await dbContext.Documentos.AsNoTracking()
            .Select(x => new { x.Id, x.TrabajadorId, x.EmpresaId, x.VehiculoId, x.TipoDocumentoId, x.FechaVencimiento, x.EstadoVigencia })
            .ToListAsync(cancellationToken);
        var propias = (await dbContext.Empresas.AsNoTracking().Where(e => e.EsPropia).Select(e => e.Id).ToListAsync(cancellationToken)).ToHashSet();
        // Una subcontrata es la Empresa que provee a otra dentro del Tenant sin ser la Empresa propia: lo dice su Relación Empresarial.
        var subcontratas = (await dbContext.RelacionesEmpresariales.AsNoTracking().Select(r => r.ProveedoraId).ToListAsync(cancellationToken))
            .Where(id => !propias.Contains(id)).ToHashSet();
        var acreditaciones = await dbContext.AcreditacionesDocumentoPlataforma.AsNoTracking()
            .Select(a => new { a.Estado, a.FechaVencimientoEnPlataforma }).ToListAsync(cancellationToken);
        var tipos = await dbContext.TiposDocumento.AsNoTracking()
            .Select(t => new { t.Id, t.Nombre, t.AmbitoAplicacion, PorDefecto = t.Requerido == RequisitoDocumental.Si }).ToListAsync(cancellationToken);
        var filasDeCentro = await dbContext.TiposDocumentoCentros.AsNoTracking().ToListAsync(cancellationToken);

        var activas = asignaciones.Where(a => a.Activa).ToList();
        bool Vencio(DateOnly? vence) => vence is { } fecha && fecha < hoy;

        // Los requisitos que bloquean el acceso, evaluados como los evalúan Inicio y el estado de los Centros.
        var evaluacion = await servicios.GetRequiredService<IEvaluacionDeAccesoPorCentroService>().EvaluarAsync([.. centroIds], cancellationToken);
        var sinCumplir = evaluacion.Requisitos.Where(r => r.Resultado.Situacion != SituacionDeRequisitoBloqueante.Cumplido).ToList();
        var ausentesQueBloquean = sinCumplir
            .Where(r => r.Resultado.Situacion == SituacionDeRequisitoBloqueante.Ausente)
            .Select(r => (r.CentroId, r.TrabajadorId, r.TipoDocumentoId)).ToHashSet();
        var bloqueadoEn = sinCumplir.Select(r => (r.TrabajadorId, r.CentroId)).ToHashSet();

        // «En tolerancia» es cómo pinta un documento vencido la ficha del Trabajador en el Centro que concede el margen.
        var centrosConTolerancia = filasDeCentro.Where(f => f.ToleranciaDias > 0).Select(f => f.CentroId).ToHashSet();
        var enTolerancia = 0;
        foreach (var trabajadorId in activas.Where(a => centrosConTolerancia.Contains(a.CentroId)).Select(a => a.TrabajadorId).Distinct())
        {
            var porCentro = await servicios.GetRequiredService<ISender>().Send(
                new ObtenerDocumentacionPorCentroDeTrabajadorQuery(trabajadorId), cancellationToken);
            enTolerancia += porCentro.Where(c => centrosConTolerancia.Contains(c.CentroId))
                .Sum(c => c.Documentos.Count(x => x.Estado == EstadoDocumento.EnTolerancia));
        }

        var visitas = await dbContext.Visitas.AsNoTracking().Where(v => !v.EstaCancelada)
            .OrderBy(v => v.FechaInicio).Select(v => new { v.Id, v.CentroId, v.FechaInicio }).ToListAsync(cancellationToken);
        var acudenA = (await dbContext.VisitasTrabajadores.AsNoTracking()
            .Select(vt => new { vt.VisitaId, vt.TrabajadorId }).ToListAsync(cancellationToken)).ToLookup(x => x.VisitaId, x => x.TrabajadorId);

        var reclamaciones = await dbContext.ReclamacionesDocumentales.AsNoTracking()
            .Where(r => r.ConversacionId != null).Select(r => new { r.ConversacionId, r.FechaEnvioUtc }).ToListAsync(cancellationToken);
        var respuestas = await dbContext.Mensajes.AsNoTracking().Where(x => x.Direccion == DireccionMensaje.Entrante)
            .Select(x => new { x.ConversacionId, x.FechaUtc }).ToListAsync(cancellationToken);
        var haceUnaSemana = hoy.AddDays(-7).ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);

        var casos = new Dictionary<string, int>
        {
            [CasosDeEstadoPilotoOutbound.Vencido] = kpis.DocumentosVencidos,
            [CasosDeEstadoPilotoOutbound.Urgente] = kpis.DocumentosUrgentes,
            [CasosDeEstadoPilotoOutbound.Proximo] = kpis.DocumentosProximos,
            [CasosDeEstadoPilotoOutbound.SinConfirmar] = kpis.DocumentosSinConfirmar,
            [CasosDeEstadoPilotoOutbound.SinCaducidad] = kpis.DocumentosSinCaducidad,
            [CasosDeEstadoPilotoOutbound.EnTolerancia] = enTolerancia,
            [CasosDeEstadoPilotoOutbound.FaltanteBloqueante] = ausentesQueBloquean.Count,
            [CasosDeEstadoPilotoOutbound.FaltanteNoBloqueante] = pares.Count(
                p => p.Estado == EstadoDocumento.Faltante && !ausentesQueBloquean.Contains((p.CentroId, p.TrabajadorId, p.TipoDocumentoId))),
            [CasosDeEstadoPilotoOutbound.AcreditacionPendienteDeSubir] = acreditaciones.Count(a => a.Estado == EstadoAcreditacion.PendienteDeSubir),
            [CasosDeEstadoPilotoOutbound.AcreditacionSubida] = acreditaciones.Count(a => a.Estado == EstadoAcreditacion.Subida),
            [CasosDeEstadoPilotoOutbound.AcreditacionAceptada] = acreditaciones.Count(
                a => a.Estado == EstadoAcreditacion.Aceptada && !Vencio(a.FechaVencimientoEnPlataforma)),
            [CasosDeEstadoPilotoOutbound.AcreditacionRechazada] = acreditaciones.Count(a => a.Estado == EstadoAcreditacion.Rechazada),
            [CasosDeEstadoPilotoOutbound.AcreditacionVencidaEnPlataforma] = acreditaciones.Count(
                a => a.Estado == EstadoAcreditacion.Aceptada && Vencio(a.FechaVencimientoEnPlataforma)),
            [CasosDeEstadoPilotoOutbound.TrabajadorBloqueadoEnUnCentroYNoEnOtro] = bloqueadoEn.Select(b => b.TrabajadorId).Distinct()
                .Count(t => activas.Any(a => a.TrabajadorId == t && !bloqueadoEn.Contains((t, a.CentroId)))),
            [CasosDeEstadoPilotoOutbound.TrabajadorDeBaja] = asignaciones.GroupBy(a => a.TrabajadorId).Count(g => g.All(a => !a.Activa)),
            [CasosDeEstadoPilotoOutbound.DocumentoDeEmpresaVencido] = documentos.Count(
                x => x.EmpresaId is { } empresaId && propias.Contains(empresaId) && Vencio(x.FechaVencimiento)),
            [CasosDeEstadoPilotoOutbound.SubcontrataConDocumentacionPropia] = subcontratas.Count(
                s => documentos.Any(x => x.EmpresaId == s)),
            [CasosDeEstadoPilotoOutbound.VehiculoConDocumentoVencido] = documentos
                .Where(x => x.VehiculoId is not null && Vencio(x.FechaVencimiento)).Select(x => x.VehiculoId).Distinct().Count(),
            [CasosDeEstadoPilotoOutbound.VisitaAMenosDe48Horas] = visitas.Count(
                v => v.FechaInicio >= d && v.FechaInicio <= d.AddDays(1) && acudenA[v.Id].Any()),
            [CasosDeEstadoPilotoOutbound.GestionPendiente] = await dbContext.Gestiones.AsNoTracking()
                .CountAsync(g => g.Estado == EstadoGestion.Pendiente, cancellationToken),
            [CasosDeEstadoPilotoOutbound.ReclamacionSinRespuesta] = reclamaciones.Count(
                r => r.FechaEnvioUtc <= haceUnaSemana && !respuestas.Any(x => x.ConversacionId == r.ConversacionId && x.FechaUtc > r.FechaEnvioUtc))
        };

        // El paquete documental: la primera Visita a un Centro cuyo canal principal es un correo, con la regla de la pantalla.
        MedicionPaquete? paquete = null;
        var visitasACentrosPorCorreo = 0;
        foreach (var visita in visitas)
        {
            if (await CanalCorreoDeCentro.ResolverAsync(dbContext, visita.CentroId, cancellationToken) is not { } canal) continue;
            if (visitasACentrosPorCorreo++ > 0) continue;

            var centro = await dbContext.Centros.AsNoTracking().Where(c => c.Id == visita.CentroId)
                .Select(c => new { c.Nombre, c.EmpresaId }).SingleAsync(cancellationToken);
            var acuden = acudenA[visita.Id].ToHashSet();
            var filas = filasDeCentro.Where(f => f.CentroId == visita.CentroId).ToDictionary(f => (f.TipoDocumentoId, f.CentroId));
            string NombreDelTipo(Guid tipoId) => tipos.SingleOrDefault(t => t.Id == tipoId)?.Nombre ?? tipoId.ToString();
            string NombreDelTrabajador(Guid? id) =>
                trabajadores.SingleOrDefault(t => t.Id == id) is { } t ? $"{t.Nombre} {t.Apellidos}" : "un Trabajador que no acude";

            var exigidosDeEmpresa = tipos
                .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Empresa
                            && ResolucionTipoDocumentoCentro.Aplica(filas, t.Id, visita.CentroId, t.PorDefecto))
                .Select(t => t.Id).ToHashSet();
            var exigidosDeTrabajador = pares
                .Where(p => p.CentroId == visita.CentroId && acuden.Contains(p.TrabajadorId))
                .Select(p => (p.TrabajadorId, p.TipoDocumentoId)).ToHashSet();

            // El mismo servicio que arma el ZIP de la pantalla de la Visita. No se usa la consulta que lo
            // envuelve porque esa deja constancia del acceso a documentos sensibles, y medir no escribe.
            var zip = await servicios.GetRequiredService<IPaqueteDocumentalVisitaService>().ConstruirAsync(visita.Id, cancellationToken);
            var enElZip = (zip?.Documentos ?? [])
                .Select(x => documentos.SingleOrDefault(o => o.Id == x.DocumentoId))
                .Where(x => x is not null).Select(x => x!).ToList();

            var (pdfDeTrabajador, pdfDeEmpresa, ficheros) = (0, 0, 0);
            if (zip is not null)
            {
                using var archivo = new ZipArchive(new MemoryStream(zip.Contenido), ZipArchiveMode.Read);
                foreach (var entrada in archivo.Entries)
                {
                    ficheros++;
                    await using var flujo = entrada.Open();
                    if (!await EmpiezaComoPdfAsync(flujo, cancellationToken)) continue;

                    if (entrada.FullName.StartsWith("Trabajadores", StringComparison.Ordinal)) pdfDeTrabajador++;
                    else if (entrada.FullName.StartsWith("Empresa", StringComparison.Ordinal)) pdfDeEmpresa++;
                }
            }

            var correosDelCanal = canal.EmailsDestinatarios.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            paquete = new MedicionPaquete(
                centro.Nombre, canal.EmailsDestinatarios,
                CorreosDelCanalFueraDeLaRegla: correosDelCanal.Length == 0 ? 1 : correosDelCanal.Count(c => !opciones.Contactos.Cumple(c)),
                DiasDesdeLaDemostracion: visita.FechaInicio.DayNumber - d.DayNumber,
                DiasDesdeHoy: visita.FechaInicio.DayNumber - hoy.DayNumber,
                Trabajadores: acuden.Count,
                ExigidosDeTrabajador: exigidosDeTrabajador.Count,
                ExigidosDeEmpresa: exigidosDeEmpresa.Count,
                PdfDeTrabajador: pdfDeTrabajador,
                PdfDeEmpresa: pdfDeEmpresa,
                FicherosEnElZip: ficheros,
                Faltan:
                [
                    .. exigidosDeEmpresa
                        .Where(tipoId => !enElZip.Any(x => x.EmpresaId == centro.EmpresaId && x.TipoDocumentoId == tipoId))
                        .Select(tipoId => $"«{NombreDelTipo(tipoId)}» de la Empresa propia").Order(StringComparer.Ordinal),
                    .. exigidosDeTrabajador
                        .Where(e => !enElZip.Any(x => x.TrabajadorId == e.TrabajadorId && x.TipoDocumentoId == e.TipoDocumentoId))
                        .Select(e => $"«{NombreDelTipo(e.TipoDocumentoId)}» de {NombreDelTrabajador(e.TrabajadorId)}").Order(StringComparer.Ordinal)
                ],
                Sobran:
                [
                    .. enElZip
                        .Where(x => x.TrabajadorId is { } trabajadorId
                            ? !exigidosDeTrabajador.Contains((trabajadorId, x.TipoDocumentoId))
                            : !(x.EmpresaId == centro.EmpresaId && exigidosDeEmpresa.Contains(x.TipoDocumentoId)))
                        .Select(x => x.TrabajadorId is null
                            ? $"«{NombreDelTipo(x.TipoDocumentoId)}» de Empresa"
                            : $"«{NombreDelTipo(x.TipoDocumentoId)}» de {NombreDelTrabajador(x.TrabajadorId)}")
                        .Order(StringComparer.Ordinal)
                ]);
        }

        return new MedicionGrande(
            TrabajadoresPropios: trabajadores.Count(t => !t.DeSubcontrata),
            TrabajadoresDeSubcontrata: trabajadores.Count(t => t.DeSubcontrata),
            Subcontratas: subcontratas.Count,
            CasosDeEstado: casos,
            VisitasACentrosPorCorreo: visitasACentrosPorCorreo,
            Paquete: paquete);
    }

    private static async Task<bool> EmpiezaComoPdfAsync(Stream flujo, CancellationToken cancellationToken)
    {
        var cabecera = new byte[5];
        return await flujo.ReadAtLeastAsync(cabecera, cabecera.Length, throwOnEndOfStream: false, cancellationToken) == cabecera.Length
               && cabecera.AsSpan().SequenceEqual("%PDF-"u8);
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
