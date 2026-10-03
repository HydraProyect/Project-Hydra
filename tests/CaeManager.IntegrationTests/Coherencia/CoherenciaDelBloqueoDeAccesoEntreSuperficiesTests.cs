using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.TiposDocumento.Queries.ObtenerToleranciasClienteEmpresarial;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionRequeridaDeCentro;
using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Coherencia;

/// <summary>
/// Bloqueo de acceso por documento bloqueante, dirigido por tabla y contra PostgreSQL con RLS real (el rol
/// <c>cae_app_runtime</c> de producción). Decisión del propietario del producto, 2026-10-03, y su corrección de la tarde:
/// <list type="number">
/// <item><b>R1</b> — un documento bloqueante de Trabajador ausente o que ya no vale bloquea a ese Trabajador en los Centros
/// que lo exigen. Un Trabajador recién dado de alta, sin ningún documento, está bloqueado (no hay «alta nueva» exenta).</item>
/// <item><b>R2</b> — un documento bloqueante de Empresa ausente o que ya no vale bloquea a TODOS los Trabajadores de esa
/// Empresa, aunque su documentación personal esté completa, pero SOLO en los Centros que ese documento exige como
/// bloqueante; nunca cruza de un Tenant a otro.</item>
/// <item><b>R3</b> — el sujeto es el Trabajador o la Empresa; «Bloqueado» es un estado del Trabajador, nunca del Centro.</item>
/// <item><b>Vigencia y tolerancia por Centro</b> — «ya no vale» se decide con las condiciones de cada Centro: su periodicidad
/// especial (sustituye al vencimiento del documento) y su tolerancia en días (la del Centro si la personaliza; si no, la
/// del Cliente empresarial titular; si no, 0). La Empresa con el mismo certificado vencido puede estar bloqueada en un
/// Centro y no en otro.</item>
/// </list>
///
/// <para>
/// Las superficies que consumen la regla (la consulta de Mi trabajo y la fusión de la cola que la pinta) tienen que
/// dar la misma respuesta que la tabla. <b>Lo que esta tabla NO fija</b>: el semáforo del Centro por las causas de
/// plataforma (acreditación rechazada o vencida en plataforma), que no son documentos de Trabajador ni de Empresa.
/// </para>
/// </summary>
public class CoherenciaDelBloqueoDeAccesoEntreSuperficiesTests : IAsyncLifetime
{
    // Tolerancia que rige para el certificado de Empresa en cada Centro de la siembra: A1 la fija en 0 (gana al 30 del
    // Cliente empresarial), A2 la personaliza en 15 y A4 la hereda del Cliente empresarial (30).
    private const int ToleranciaA1 = 0;
    private const int ToleranciaA2 = 15;
    private const int ToleranciaA4 = 30;

    // Otros dos Clientes empresariales del mismo Tenant: el A2 fija un defecto distinto (5) y el A3 no fija ninguno (0). Sus
    // Centros (A6 y A7) heredan cada uno el suyo; si el servicio tomara la tolerancia de otro Cliente empresarial, fallaria.
    private const int ToleranciaA6 = 5;
    private const int ToleranciaA7 = 0;

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _tenantC = Guid.NewGuid();
    private readonly Guid _tenantD = Guid.NewGuid();
    private readonly DateOnly _hoy = DiaDeNegocio.Hoy();
    private readonly Guid _usuario = Guid.NewGuid();
    private static int _contadorDni = 10_000_000;

    private CaeManagerDbContext _propietario = null!;
    private readonly List<CaeManagerDbContext> _contextosRuntime = [];

    private Guid _clienteA, _centroA1, _centroA2, _centroA3SinGestion, _centroA4, _centroA5, _centroA6, _centroA7;
    private Guid _tipoPss, _tipoCertificado;
    private readonly List<Caso> _casos = [];

    private sealed record Esperado(AmbitoAplicacion Ambito, SituacionDeRequisitoBloqueante Situacion, int ToleranciaDias);

    private static Esperado EsperadoDeTrabajador(SituacionDeRequisitoBloqueante situacion) => new(AmbitoAplicacion.Trabajador, situacion, 0);
    private static Esperado EmpresaAusente(int tolerancia) => new(AmbitoAplicacion.Empresa, SituacionDeRequisitoBloqueante.Ausente, tolerancia);
    private static Esperado EmpresaVencida(int tolerancia) => new(AmbitoAplicacion.Empresa, SituacionDeRequisitoBloqueante.Vencido, tolerancia);

    /// <summary>Una fila de la tabla: un Trabajador, dónde está asignado y a qué Centros y por qué debe estar bloqueado.</summary>
    private sealed class Caso(string nombre, string superficie)
    {
        public string Nombre { get; } = nombre;
        public string Superficie { get; } = superficie;
        public Guid TrabajadorId { get; set; }

        /// <summary>Centro → lo esperado en él; <c>null</c> = no debe aparecer.</summary>
        public Dictionary<Guid, Esperado?> PorCentro { get; } = [];
    }

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        await SembrarTenantAAsync();
        await SembrarTenantBAsync();
        await SembrarTenantCAsync();
        await SembrarTenantDAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var contexto in _contextosRuntime)
            await contexto.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    // ---------- Siembra ----------

    private static string Dni()
    {
        var numero = Interlocked.Increment(ref _contadorDni);
        return $"{numero:D8}{"TRWAGMYFPDXBNJZSQVHLCKE"[numero % 23]}";
    }

    private async Task SembrarTenantAAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantA);
        var c = _propietario;

        var cliente = Empresa.CrearComoCliente("Cliente empresarial A", "B12345674", false, null, null);
        var contratista = new Empresa("Contratista A S.L.");
        var cliente2 = Empresa.CrearComoCliente("Cliente empresarial A2", "B23456783", false, null, null);
        var cliente3 = Empresa.CrearComoCliente("Cliente empresarial A3", "B34567891", false, null, null);
        var empVencida5 = new Empresa("Empresa con certificado vencido hace 5 dias");
        var empVencida6 = new Empresa("Empresa con certificado vencido hace 6 dias");
        var empresaBase = new Empresa("Empresa base A");
        var empAusente = new Empresa("Empresa sin certificado");
        var empVencida1 = new Empresa("Empresa con certificado vencido hace 1 dia");
        var empVencida15 = new Empresa("Empresa con certificado vencido hace 15 dias");
        var empVencida16 = new Empresa("Empresa con certificado vencido hace 16 dias");
        var empVencida30 = new Empresa("Empresa con certificado vencido hace 30 dias");
        var empVencida31 = new Empresa("Empresa con certificado vencido hace 31 dias");
        var empVenceHoy = new Empresa("Empresa con certificado que vence hoy");
        var empNoCaduca = new Empresa("Empresa con certificado que no caduca");
        var empSinConfirmar = new Empresa("Empresa con certificado sin confirmar");
        var empVigente = new Empresa("Empresa con certificado vigente");
        var subAusente = new Empresa("Subcontratista sin certificado");
        var subVigente = new Empresa("Subcontratista con certificado");
        c.Empresas.AddRange(cliente2, cliente3, empVencida5, empVencida6);
        c.Empresas.AddRange(cliente, contratista, empresaBase, empAusente, empVencida1, empVencida15, empVencida16,
            empVencida30, empVencida31, empVenceHoy, empNoCaduca, empSinConfirmar, empVigente, subAusente, subVigente);
        await c.SaveChangesAsync();

        var centro1 = new Centro(cliente.Id, contratista.Id, "Centro A1");
        var centro2 = new Centro(cliente.Id, contratista.Id, "Centro A2");
        var centro3 = new Centro(cliente.Id, contratista.Id, "Centro A3 sin gestion CAE");
        centro3.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
        var centro4 = new Centro(cliente.Id, contratista.Id, "Centro A4");
        var centro5 = new Centro(cliente.Id, contratista.Id, "Centro A5");
        var centro6 = new Centro(cliente2.Id, contratista.Id, "Centro A6 (Cliente empresarial A2)");
        var centro7 = new Centro(cliente3.Id, contratista.Id, "Centro A7 (Cliente empresarial A3)");
        c.Centros.AddRange(centro1, centro2, centro3, centro4, centro5, centro6, centro7);

        var pss = new TipoDocumento("PSS firmado", null, false, 1, AmbitoAplicacion.Trabajador);
        var certificado = new TipoDocumento("Certificado de la Seguridad Social", null, false, 2, AmbitoAplicacion.Empresa);
        var deCliente = new TipoDocumento("Tipo de ambito Cliente", null, false, 3, AmbitoAplicacion.Cliente);
        c.TiposDocumento.AddRange(pss, certificado, deCliente);
        await c.SaveChangesAsync();

        // El PSS es bloqueante en el Centro 1 y en el 5 (este con renovacion cada 12 meses). El certificado de Empresa lo es
        // en los Centros 1 (tolerancia 0 propia), 2 (15 propia) y 4 (sin propia: hereda 30 del Cliente empresarial); en el 3,
        // sin gestion CAE, la marca no cuenta; en el 5 no se exige. El tipo de ambito Cliente tambien esta marcado: no tiene
        // sujeto y no debe bloquear a nadie.
        c.TiposDocumentoCentros.AddRange(
            new TipoDocumentoCentro(pss.Id, centro1.Id, incluido: true, bloqueaAcceso: true),
            new TipoDocumentoCentro(pss.Id, centro3.Id, incluido: true, bloqueaAcceso: true),
            new TipoDocumentoCentro(pss.Id, centro5.Id, incluido: true, periodicidadEspecialMeses: 12, bloqueaAcceso: true),
            new TipoDocumentoCentro(certificado.Id, centro1.Id, incluido: true, bloqueaAcceso: true, toleranciaDias: ToleranciaA1),
            new TipoDocumentoCentro(certificado.Id, centro2.Id, incluido: true, bloqueaAcceso: true, toleranciaDias: ToleranciaA2),
            new TipoDocumentoCentro(certificado.Id, centro3.Id, incluido: true, bloqueaAcceso: true, toleranciaDias: ToleranciaA2),
            new TipoDocumentoCentro(certificado.Id, centro4.Id, incluido: true, bloqueaAcceso: true),
            new TipoDocumentoCentro(certificado.Id, centro6.Id, incluido: true, bloqueaAcceso: true),
            new TipoDocumentoCentro(certificado.Id, centro7.Id, incluido: true, bloqueaAcceso: true),
            new TipoDocumentoCentro(deCliente.Id, centro1.Id, incluido: true, bloqueaAcceso: true));
        c.ToleranciasDocumentoClienteEmpresarial.Add(new ToleranciaDocumentoClienteEmpresarial(cliente.Id, certificado.Id, ToleranciaA4));
        c.ToleranciasDocumentoClienteEmpresarial.Add(new ToleranciaDocumentoClienteEmpresarial(cliente2.Id, certificado.Id, ToleranciaA6));
        await c.SaveChangesAsync();

        _clienteA = cliente.Id;
        _centroA1 = centro1.Id;
        _centroA2 = centro2.Id;
        _centroA3SinGestion = centro3.Id;
        _centroA4 = centro4.Id;
        _centroA5 = centro5.Id;
        _centroA6 = centro6.Id;
        _centroA7 = centro7.Id;
        _tipoPss = pss.Id;
        _tipoCertificado = certificado.Id;

        var emision = _hoy.AddDays(-400);

        // La Empresa base y las de «vigente» cumplen el certificado: no bloquean a sus Trabajadores por R2.
        c.Documentos.Add(Documento.DeEmpresa(empresaBase.Id, certificado.Id, emision, VigenciaDocumento.NoCaduca));
        c.Documentos.Add(Documento.DeEmpresa(empVencida1.Id, certificado.Id, emision, VigenciaDocumento.VenceEl(_hoy.AddDays(-1))));
        c.Documentos.Add(Documento.DeEmpresa(empVencida15.Id, certificado.Id, emision, VigenciaDocumento.VenceEl(_hoy.AddDays(-15))));
        c.Documentos.Add(Documento.DeEmpresa(empVencida16.Id, certificado.Id, emision, VigenciaDocumento.VenceEl(_hoy.AddDays(-16))));
        c.Documentos.Add(Documento.DeEmpresa(empVencida30.Id, certificado.Id, emision, VigenciaDocumento.VenceEl(_hoy.AddDays(-30))));
        c.Documentos.Add(Documento.DeEmpresa(empVencida31.Id, certificado.Id, emision, VigenciaDocumento.VenceEl(_hoy.AddDays(-31))));
        c.Documentos.Add(Documento.DeEmpresa(empVencida5.Id, certificado.Id, emision, VigenciaDocumento.VenceEl(_hoy.AddDays(-5))));
        c.Documentos.Add(Documento.DeEmpresa(empVencida6.Id, certificado.Id, emision, VigenciaDocumento.VenceEl(_hoy.AddDays(-6))));
        c.Documentos.Add(Documento.DeEmpresa(empVenceHoy.Id, certificado.Id, emision, VigenciaDocumento.VenceEl(_hoy)));
        c.Documentos.Add(Documento.DeEmpresa(empNoCaduca.Id, certificado.Id, emision, VigenciaDocumento.NoCaduca));
        c.Documentos.Add(Documento.DeEmpresa(empSinConfirmar.Id, certificado.Id, emision, VigenciaDocumento.SinConfirmar));
        c.Documentos.Add(Documento.DeEmpresa(empVigente.Id, certificado.Id, emision, VigenciaDocumento.VenceEl(_hoy.AddDays(200))));
        c.Documentos.Add(Documento.DeEmpresa(subVigente.Id, certificado.Id, emision, VigenciaDocumento.VenceEl(_hoy.AddDays(200))));
        await c.SaveChangesAsync();

        var ausente = EsperadoDeTrabajador(SituacionDeRequisitoBloqueante.Ausente);
        var vencido = EsperadoDeTrabajador(SituacionDeRequisitoBloqueante.Vencido);

        // R1: un Trabajador de la Empresa base, asignado solo al Centro 1, con el PSS en cada situacion. El primero es el
        // recien dado de alta: sin ningun documento, bloqueado.
        await AnadirTrabajadorConPssAsync(c, empresaBase.Id, "R1 · alta nueva sin ningun documento", emision, [], (_centroA1, ausente));
        await AnadirTrabajadorConPssAsync(c, empresaBase.Id, "R1 · PSS vencido ayer", emision, [VigenciaDocumento.VenceEl(_hoy.AddDays(-1))], (_centroA1, vencido));
        await AnadirTrabajadorConPssAsync(c, empresaBase.Id, "R1 · PSS vence hoy (Urgente, valido hoy)", emision, [VigenciaDocumento.VenceEl(_hoy)], (_centroA1, null));
        await AnadirTrabajadorConPssAsync(c, empresaBase.Id, "R1 · PSS Urgente a 5 dias", emision, [VigenciaDocumento.VenceEl(_hoy.AddDays(5))], (_centroA1, null));
        await AnadirTrabajadorConPssAsync(c, empresaBase.Id, "R1 · PSS Proximo a 20 dias", emision, [VigenciaDocumento.VenceEl(_hoy.AddDays(20))], (_centroA1, null));
        await AnadirTrabajadorConPssAsync(c, empresaBase.Id, "R1 · PSS vigente a 200 dias", emision, [VigenciaDocumento.VenceEl(_hoy.AddDays(200))], (_centroA1, null));
        await AnadirTrabajadorConPssAsync(c, empresaBase.Id, "R1 · PSS No caduca", emision, [VigenciaDocumento.NoCaduca], (_centroA1, null));
        await AnadirTrabajadorConPssAsync(c, empresaBase.Id, "R1 · PSS Sin confirmar", emision, [VigenciaDocumento.SinConfirmar], (_centroA1, null));
        await AnadirTrabajadorConPssAsync(c, empresaBase.Id, "R1 · PSS vencido y su renovacion vigente", emision,
            [VigenciaDocumento.VenceEl(_hoy.AddDays(-30)), VigenciaDocumento.VenceEl(_hoy.AddDays(335))], (_centroA1, null));

        // Vigencia por Centro: el Centro 5 exige renovar el PSS cada 12 meses desde la emision; el Centro 1 vale el
        // vencimiento del propio documento.
        await AnadirTrabajadorConPssAsync(c, empresaBase.Id, "Vigencia · PSS emitido hace 400 dias que vence en 200: vale en A1, vencido en A5 (12 meses)",
            emision, [VigenciaDocumento.VenceEl(_hoy.AddDays(200))], (_centroA1, null), (_centroA5, vencido));
        await AnadirTrabajadorConPssAsync(c, empresaBase.Id, "Vigencia · PSS emitido hace 100 dias vencido hace 5: vencido en A1, vale en A5 (la periodicidad sustituye al vencimiento)",
            _hoy.AddDays(-100), [VigenciaDocumento.VenceEl(_hoy.AddDays(-5))], (_centroA1, vencido), (_centroA5, null));
        await AnadirTrabajadorConPssAsync(c, empresaBase.Id, "Vigencia · PSS No caduca no vence en ningun Centro, tampoco con periodicidad",
            emision, [VigenciaDocumento.NoCaduca], (_centroA1, null), (_centroA5, null));
        await AnadirTrabajadorConPssAsync(c, empresaBase.Id, "Vigencia · sin PSS: ausente en A1 y en A5",
            emision, [], (_centroA1, ausente), (_centroA5, ausente));

        // R2: un Trabajador con el PSS completo, asignado a los Centros 1, 2, 3 (sin gestion CAE) y 4, de cada Empresa. El
        // certificado bloquea solo donde se exige y segun la tolerancia de cada Centro: [A1 (0), A2 (15), A4 (30)].
        await AnadirR2Async(c, "R2 · Empresa sin el certificado", empAusente.Id, false,
            EmpresaAusente(ToleranciaA1), EmpresaAusente(ToleranciaA2), EmpresaAusente(ToleranciaA4));
        await AnadirR2Async(c, "R2 · certificado de Empresa vencido hace 1 dia", empVencida1.Id, false,
            EmpresaVencida(ToleranciaA1), null, null);
        await AnadirR2Async(c, "R2 · certificado de Empresa vencido hace 15 dias (limite de A2)", empVencida15.Id, false,
            EmpresaVencida(ToleranciaA1), null, null);
        await AnadirR2Async(c, "R2 · certificado de Empresa vencido hace 16 dias", empVencida16.Id, false,
            EmpresaVencida(ToleranciaA1), EmpresaVencida(ToleranciaA2), null);
        await AnadirR2Async(c, "R2 · certificado de Empresa vencido hace 30 dias (limite de A4)", empVencida30.Id, false,
            EmpresaVencida(ToleranciaA1), EmpresaVencida(ToleranciaA2), null);
        await AnadirR2Async(c, "R2 · certificado de Empresa vencido hace 31 dias", empVencida31.Id, false,
            EmpresaVencida(ToleranciaA1), EmpresaVencida(ToleranciaA2), EmpresaVencida(ToleranciaA4));
        await AnadirR2Async(c, "R2 · certificado de Empresa que vence hoy", empVenceHoy.Id, false, null, null, null);
        await AnadirR2Async(c, "R2 · certificado de Empresa que no caduca", empNoCaduca.Id, false, null, null, null);
        await AnadirR2Async(c, "R2 · certificado de Empresa sin confirmar", empSinConfirmar.Id, false, null, null, null);
        await AnadirR2Async(c, "R2 · certificado de Empresa vigente", empVigente.Id, false, null, null, null);
        await AnadirR2Async(c, "R2 · Trabajador de un subcontratista sin el certificado", subAusente.Id, true,
            EmpresaAusente(ToleranciaA1), EmpresaAusente(ToleranciaA2), EmpresaAusente(ToleranciaA4));
        await AnadirR2Async(c, "R2 · Trabajador de un subcontratista con el certificado", subVigente.Id, true, null, null, null);

        // Cada Centro hereda el defecto de SU Cliente empresarial: A4 (Cliente A, 30), A6 (Cliente A2, 5) y A7 (Cliente A3, sin
        // defecto: 0). Un certificado vencido hace 5 dias vale en A4 y A6 y no en A7; hace 6, solo en A4.
        await AnadirHerenciaAsync(c, "Herencia · certificado de Empresa vencido hace 5 dias", empVencida5.Id,
            null, EmpresaVencida(ToleranciaA7));
        await AnadirHerenciaAsync(c, "Herencia · certificado de Empresa vencido hace 6 dias", empVencida6.Id,
            EmpresaVencida(ToleranciaA6), EmpresaVencida(ToleranciaA7));

        // Control: el requisito de Trabajador es de SU Centro. En el Centro 2 el PSS no es bloqueante y nadie tiene
        // que aparecer por no tenerlo.
        var sinPssEnOtroCentro = Trabajador.DeEmpresa(empresaBase.Id, "Control", "Centro dos", Dni());
        c.Trabajadores.Add(sinPssEnOtroCentro);
        await c.SaveChangesAsync();
        c.Asignaciones.Add(new Asignacion(sinPssEnOtroCentro.Id, centro2.Id, emision));
        await c.SaveChangesAsync();
        var control = new Caso("Control · sin PSS pero solo en el Centro 2, donde no es bloqueante", "Trabajador")
        {
            TrabajadorId = sinPssEnOtroCentro.Id
        };
        control.PorCentro[centro2.Id] = null;
        _casos.Add(control);

        // Control: una asignacion dada de baja no bloquea en ese Centro.
        var deBaja = Trabajador.DeEmpresa(empAusente.Id, "Control", "De baja", Dni());
        c.Trabajadores.Add(deBaja);
        await c.SaveChangesAsync();
        var asignacionBaja = new Asignacion(deBaja.Id, centro2.Id, emision);
        c.Asignaciones.Add(asignacionBaja);
        await c.SaveChangesAsync();
        asignacionBaja.DarDeBaja(_hoy.AddDays(-1));
        await c.SaveChangesAsync();
        var casoBaja = new Caso("Control · Trabajador de una Empresa sin certificado pero con la asignacion dada de baja", "Trabajador")
        {
            TrabajadorId = deBaja.Id
        };
        casoBaja.PorCentro[centro2.Id] = null;
        _casos.Add(casoBaja);
    }

    private async Task AnadirTrabajadorConPssAsync(
        CaeManagerDbContext c, Guid empresaId, string nombre, DateOnly emision, VigenciaDocumento[] vigencias,
        params (Guid Centro, Esperado? Esperado)[] porCentro)
    {
        var trabajador = Trabajador.DeEmpresa(empresaId, "Caso", nombre, Dni());
        c.Trabajadores.Add(trabajador);
        await c.SaveChangesAsync();
        foreach (var (centro, _) in porCentro)
            c.Asignaciones.Add(new Asignacion(trabajador.Id, centro, _hoy.AddDays(-400)));
        foreach (var vigencia in vigencias)
            c.Documentos.Add(Documento.DeTrabajador(trabajador.Id, _tipoPss, emision, vigencia));
        await c.SaveChangesAsync();

        var caso = new Caso(nombre, "Trabajador") { TrabajadorId = trabajador.Id };
        foreach (var (centro, esperado) in porCentro)
            caso.PorCentro[centro] = esperado;
        _casos.Add(caso);
    }

    private async Task AnadirR2Async(
        CaeManagerDbContext c, string nombre, Guid empresaId, bool subcontrata,
        Esperado? enA1, Esperado? enA2, Esperado? enA4)
    {
        var trabajador = subcontrata
            ? Trabajador.DeSubcontrata(empresaId, "Caso", nombre, Dni())
            : Trabajador.DeEmpresa(empresaId, "Caso", nombre, Dni());
        c.Trabajadores.Add(trabajador);
        await c.SaveChangesAsync();

        var emision = _hoy.AddDays(-400);
        // Documentacion personal COMPLETA: el PSS vigente. R2: bloquea igual donde el certificado se exige.
        c.Documentos.Add(Documento.DeTrabajador(trabajador.Id, _tipoPss, emision, VigenciaDocumento.VenceEl(_hoy.AddDays(200))));
        foreach (var centro in new[] { _centroA1, _centroA2, _centroA3SinGestion, _centroA4 })
            c.Asignaciones.Add(new Asignacion(trabajador.Id, centro, emision));
        await c.SaveChangesAsync();

        var caso = new Caso(nombre, "Empresa") { TrabajadorId = trabajador.Id };
        caso.PorCentro[_centroA1] = enA1;
        caso.PorCentro[_centroA2] = enA2;
        caso.PorCentro[_centroA3SinGestion] = null; // un Centro sin gestion CAE no exige nada, tampoco por R2
        caso.PorCentro[_centroA4] = enA4;
        _casos.Add(caso);
    }

    private async Task AnadirHerenciaAsync(CaeManagerDbContext c, string nombre, Guid empresaId, Esperado? enA6, Esperado? enA7)
    {
        var trabajador = Trabajador.DeEmpresa(empresaId, "Caso", nombre, Dni());
        c.Trabajadores.Add(trabajador);
        await c.SaveChangesAsync();

        var emision = _hoy.AddDays(-400);
        foreach (var centro in new[] { _centroA4, _centroA6, _centroA7 })
            c.Asignaciones.Add(new Asignacion(trabajador.Id, centro, emision));
        await c.SaveChangesAsync();

        var caso = new Caso(nombre, "Empresa") { TrabajadorId = trabajador.Id };
        caso.PorCentro[_centroA4] = null; // 30 dias de su Cliente empresarial: vale
        caso.PorCentro[_centroA6] = enA6;
        caso.PorCentro[_centroA7] = enA7;
        _casos.Add(caso);
    }

    /// <summary>
    /// Tenant B: su Empresa no tiene el certificado y el tipo es bloqueante en B. Su Trabajador esta bloqueado en B y
    /// nunca debe verse desde otro Tenant. Su Cliente empresarial fija una tolerancia de 100 dias para el Tipo: es la que
    /// rige en B (no la de 30 del Tenant A) y ninguna fila de tolerancia cruza de un Tenant a otro.
    /// </summary>
    private async Task SembrarTenantBAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantB);
        var c = _propietario;

        var cliente = Empresa.CrearComoCliente("Cliente empresarial B", "B12345674", false, null, null);
        var contratista = new Empresa("Contratista B S.L.");
        c.Empresas.AddRange(cliente, contratista);
        await c.SaveChangesAsync();

        var centro = new Centro(cliente.Id, contratista.Id, "Centro B1");
        var certificado = new TipoDocumento("Certificado de la Seguridad Social", null, false, 1, AmbitoAplicacion.Empresa);
        c.Centros.Add(centro);
        c.TiposDocumento.Add(certificado);
        await c.SaveChangesAsync();
        c.TiposDocumentoCentros.Add(new TipoDocumentoCentro(certificado.Id, centro.Id, incluido: true, bloqueaAcceso: true));
        c.ToleranciasDocumentoClienteEmpresarial.Add(new ToleranciaDocumentoClienteEmpresarial(cliente.Id, certificado.Id, 100));

        var trabajador = Trabajador.DeEmpresa(contratista.Id, "Trabajador", "De B", Dni());
        c.Trabajadores.Add(trabajador);
        await c.SaveChangesAsync();
        c.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, _hoy.AddDays(-400)));
        await c.SaveChangesAsync();

        _clienteB = cliente.Id;
        _centroB = centro.Id;
        _trabajadorB = trabajador.Id;
    }

    private Guid _clienteB, _centroB, _trabajadorB, _centroC, _trabajadorC;

    /// <summary>
    /// Tenant C: tiene el mismo tipo de certificado, su Empresa tampoco lo tiene, pero el tipo NO es bloqueante en C.
    /// Que sea bloqueante en B no puede alcanzarle: nadie en C debe quedar bloqueado.
    /// </summary>
    private async Task SembrarTenantCAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantC);
        var c = _propietario;

        var cliente = Empresa.CrearComoCliente("Cliente empresarial C", "B12345674", false, null, null);
        var contratista = new Empresa("Contratista C S.L.");
        c.Empresas.AddRange(cliente, contratista);
        await c.SaveChangesAsync();

        var centro = new Centro(cliente.Id, contratista.Id, "Centro C1");
        var certificado = new TipoDocumento("Certificado de la Seguridad Social", null, false, 1, AmbitoAplicacion.Empresa);
        c.Centros.Add(centro);
        c.TiposDocumento.Add(certificado);
        await c.SaveChangesAsync();

        var trabajador = Trabajador.DeEmpresa(contratista.Id, "Trabajador", "De C", Dni());
        c.Trabajadores.Add(trabajador);
        await c.SaveChangesAsync();
        c.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, _hoy.AddDays(-400)));
        await c.SaveChangesAsync();

        _centroC = centro.Id;
        _trabajadorC = trabajador.Id;
    }

    private Guid _trabajadorD, _centroDSinGestion;

    /// <summary>
    /// Tenant D: el certificado de Empresa esta marcado como bloqueante SOLO en un Centro sin gestion CAE. Ese Centro
    /// no exige nada, asi que su marca no declara requisito: el Trabajador (sin certificado en su Empresa) asignado a
    /// otro Centro de D, con gestion CAE, no debe quedar bloqueado por ella.
    /// </summary>
    private async Task SembrarTenantDAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantD);
        var c = _propietario;

        var cliente = Empresa.CrearComoCliente("Cliente empresarial D", "B12345674", false, null, null);
        var contratista = new Empresa("Contratista D S.L.");
        c.Empresas.AddRange(cliente, contratista);
        await c.SaveChangesAsync();

        var centroConGestion = new Centro(cliente.Id, contratista.Id, "Centro D1");
        var centroSinGestion = new Centro(cliente.Id, contratista.Id, "Centro D2 sin gestion CAE");
        centroSinGestion.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
        var certificado = new TipoDocumento("Certificado de la Seguridad Social", null, false, 1, AmbitoAplicacion.Empresa);
        c.Centros.AddRange(centroConGestion, centroSinGestion);
        c.TiposDocumento.Add(certificado);
        await c.SaveChangesAsync();
        c.TiposDocumentoCentros.Add(new TipoDocumentoCentro(certificado.Id, centroSinGestion.Id, incluido: true, bloqueaAcceso: true));

        var trabajador = Trabajador.DeEmpresa(contratista.Id, "Trabajador", "De D", Dni());
        c.Trabajadores.Add(trabajador);
        await c.SaveChangesAsync();
        c.Asignaciones.Add(new Asignacion(trabajador.Id, centroConGestion.Id, _hoy.AddDays(-400)));
        await c.SaveChangesAsync();

        _centroDSinGestion = centroSinGestion.Id;
        _trabajadorD = trabajador.Id;
    }

    // ---------- Lectura bajo RLS ----------

    private CaeManagerDbContext Runtime(Guid tenant)
    {
        var tenantDeLaPeticion = new TenantActualDeLaPeticion(tenant);
        var usuario = new CurrentUserServiceFalso(_usuario, tenantOrigenId: tenant);
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(tenantDeLaPeticion, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        var contexto = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantDeLaPeticion);
        _contextosRuntime.Add(contexto);
        return contexto;
    }

    private static Task<IReadOnlyList<DocumentacionBloqueantePendienteDto>> MiTrabajo(
        CaeManagerDbContext c, IAlcanceDatosService? alcance = null)
    {
        var alcanceEfectivo = alcance ?? new AlcanceDatosServiceFalso();
        var evaluacion = new EvaluacionDeAccesoPorCentroService(c, c, c, c, c, alcanceEfectivo);
        return new ObtenerDocumentacionBloqueantePendienteQueryHandler(c, c, c, evaluacion)
            .Handle(new ObtenerDocumentacionBloqueantePendienteQuery(), CancellationToken.None);
    }

    // ---------- La tabla ----------

    [Fact]
    public async Task Control_la_lectura_va_bajo_RLS_efectiva()
    {
        // Sin este control, «el Tenant A no ve lo de B» pasaria tambien sobre una conexion que se salta RLS.
        var comoA = Runtime(_tenantA);
        (await comoA.Centros.IgnoreQueryFilters().CountAsync(x => x.Id == _centroB)).Should().Be(0);
        (await comoA.Centros.IgnoreQueryFilters().CountAsync(x => x.Id == _centroA1)).Should().Be(1);
    }

    [Fact]
    public async Task La_tolerancia_del_Cliente_empresarial_es_del_Tenant_propietario_y_RLS_la_aisla()
    {
        // La tabla nueva tiene RLS por Tenant: cada Tenant ve solo sus filas, aunque se salten los filtros de EF.
        var delA = await Runtime(_tenantA).ToleranciasDocumentoClienteEmpresarial.IgnoreQueryFilters()
            .Select(t => new { t.ClienteEmpresarialId, t.ToleranciaDias }).ToListAsync();
        delA.Should().HaveCount(2).And.Contain(new { ClienteEmpresarialId = _clienteA, ToleranciaDias = ToleranciaA4 });
        delA.Select(t => t.ToleranciaDias).Should().BeEquivalentTo([ToleranciaA4, ToleranciaA6]);

        var delB = await Runtime(_tenantB).ToleranciasDocumentoClienteEmpresarial.IgnoreQueryFilters()
            .Select(t => new { t.ClienteEmpresarialId, t.ToleranciaDias }).ToListAsync();
        delB.Should().ContainSingle().Which.Should().Be(new { ClienteEmpresarialId = _clienteB, ToleranciaDias = 100 });

        (await Runtime(_tenantC).ToleranciasDocumentoClienteEmpresarial.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public void Control_la_siembra_aporta_al_menos_un_bloqueo_y_un_no_bloqueo_de_cada_regla()
    {
        // Que la tabla pueda fallar: sin filas bloqueadas ni no bloqueadas de R1 y de R2, «todo coincide» seria vacio.
        _casos.Where(c => c.Superficie == "Trabajador").SelectMany(c => c.PorCentro.Values).Should().Contain(v => v != null).And.Contain(v => v == null);
        _casos.Where(c => c.Superficie == "Empresa").SelectMany(c => c.PorCentro.Values).Should().Contain(v => v != null).And.Contain(v => v == null);
    }

    [Fact]
    public void Control_hay_una_misma_Empresa_bloqueada_en_un_Centro_y_no_en_otro()
    {
        // El caso que motiva la regla por Centro: mismo certificado, misma Empresa, tolerancias distintas.
        var caso = _casos.Single(c => c.Nombre == "R2 · certificado de Empresa vencido hace 16 dias");
        caso.PorCentro[_centroA1].Should().NotBeNull();
        caso.PorCentro[_centroA2].Should().NotBeNull();
        caso.PorCentro[_centroA4].Should().BeNull();
    }

    [Fact]
    public async Task La_misma_documentacion_da_el_mismo_bloqueo_en_Mi_trabajo_y_en_la_cola()
    {
        var pendientes = await MiTrabajo(Runtime(_tenantA));
        var fallos = new List<string>();

        // Superficie 1: la consulta de Mi trabajo, exactamente lo que dice la tabla.
        foreach (var caso in _casos)
        {
            foreach (var (centro, esperado) in caso.PorCentro)
            {
                var filas = pendientes.Where(p => p.TrabajadorId == caso.TrabajadorId && p.CentroId == centro).ToList();
                if (esperado is null)
                {
                    if (filas.Count != 0)
                        fallos.Add($"{caso.Nombre} · Centro {Nombre(centro)}: no debia estar bloqueado y aparece {Describir(filas)}");
                    continue;
                }

                if (filas.Count != 1)
                {
                    fallos.Add($"{caso.Nombre} · Centro {Nombre(centro)}: esperada 1 fila, observadas {filas.Count} {Describir(filas)}");
                    continue;
                }

                var fila = filas[0];
                if (fila.Ambito != esperado.Ambito || fila.Situacion != esperado.Situacion || fila.ToleranciaDias != esperado.ToleranciaDias)
                    fallos.Add($"{caso.Nombre} · Centro {Nombre(centro)}: esperado {esperado}, observado {Describir([fila])}");

                var tipoEsperado = esperado.Ambito == AmbitoAplicacion.Trabajador ? _tipoPss : _tipoCertificado;
                if (fila.TipoDocumentoId != tipoEsperado)
                    fallos.Add($"{caso.Nombre} · Centro {Nombre(centro)}: el requisito no es el del ambito {esperado.Ambito}");

                // Un Vencido lleva el dia en que venció en ese Centro; un Ausente no.
                if ((fila.Situacion == SituacionDeRequisitoBloqueante.Vencido) != fila.VencimientoEfectivo.HasValue)
                    fallos.Add($"{caso.Nombre} · Centro {Nombre(centro)}: VencimientoEfectivo={fila.VencimientoEfectivo} no es coherente con {fila.Situacion}");
            }
        }

        // Nada fuera de la tabla: ninguna fila de un Trabajador que la tabla no conozca ni de un tipo sin sujeto (Cliente).
        var conocidos = _casos.Select(c => c.TrabajadorId).ToHashSet();
        foreach (var fila in pendientes.Where(p => !conocidos.Contains(p.TrabajadorId)))
            fallos.Add($"Fila fuera de la tabla: {Describir([fila])}");
        pendientes.Select(p => p.TipoDocumentoId).Distinct().Should().BeSubsetOf([_tipoPss, _tipoCertificado],
            "un tipo de ambito Cliente marcado como bloqueante no tiene sujeto y no bloquea a nadie");

        // Superficie 2: la fusion de la cola de Mi trabajo, que decide el titulo y si bloquea de verdad.
        var cola = ObtenerBandejaGestorQueryHandler.Fusionar([], [], pendientes, [], [], [], [], _hoy, 24, 4);
        cola.Should().HaveCount(pendientes.Count);
        foreach (var fila in pendientes)
        {
            var item = cola.Single(i => i.Id == $"requisito-{fila.CentroId}-{fila.TrabajadorId}-{fila.TipoDocumentoId}");
            ObtenerBandejaAgrupadaQueryHandler.BloqueaElAcceso(item).Should().BeTrue(
                "toda fila de Mi trabajo es un Trabajador bloqueado: no hay excepcion de alta nueva");

            if (fila.Ambito == AmbitoAplicacion.Empresa)
            {
                item.Titulo.Should().Contain("(Empresa ").And.Contain(fila.EmpresaNombre!,
                    "el titulo dice que el documento que falta es de la Empresa, no del Trabajador");
            }
            else
            {
                item.Titulo.Should().NotContain("(Empresa ");
            }
        }

        fallos.Should().BeEmpty("todas las superficies tienen que dar lo mismo que la tabla:\n" + string.Join("\n", fallos));
    }

    /// <summary>
    /// Centro 360 por Trabajador: el detalle de ESE Centro es exactamente lo que Mi trabajo dice de ese Centro (la misma regla
    /// por Centro, los mismos datos), no una segunda lectura. Todos los Centros juntos reconstruyen Mi trabajo, y un Centro de
    /// otro Tenant no devuelve nada bajo RLS.
    /// </summary>
    [Fact]
    public async Task El_detalle_por_Trabajador_de_un_Centro_es_Mi_trabajo_restringido_a_ese_Centro()
    {
        var comoA = Runtime(_tenantA);
        var miTrabajo = await MiTrabajo(comoA);
        var evaluacion = new EvaluacionDeAccesoPorCentroService(comoA, comoA, comoA, comoA, comoA, new AlcanceDatosServiceFalso());
        var handler = new ObtenerDocumentacionBloqueantePendienteQueryHandler(comoA, comoA, comoA, evaluacion);

        miTrabajo.Select(p => p.CentroId).Distinct().Should().HaveCountGreaterThan(1,
            "control del instrumento: hay bloqueos en varios Centros, así que filtrar por uno distingue algo");

        var reconstruido = new List<DocumentacionBloqueantePendienteDto>();
        foreach (var centro in miTrabajo.Select(p => p.CentroId).Distinct())
        {
            var delCentro = await handler.Handle(new ObtenerDocumentacionBloqueantePendienteQuery(centro), CancellationToken.None);
            delCentro.Select(f => f.CentroId).Distinct().Should().Equal([centro], "solo ese Centro");
            delCentro.Should().BeEquivalentTo(miTrabajo.Where(p => p.CentroId == centro),
                $"el detalle del Centro {Nombre(centro)} es lo que Mi trabajo dice de ese Centro");
            reconstruido.AddRange(delCentro);
        }

        reconstruido.Should().BeEquivalentTo(miTrabajo, "todos los Centros juntos reconstruyen Mi trabajo");

        // Un Centro de otro Tenant, pedido desde A: RLS lo oculta y no hay filas.
        (await handler.Handle(new ObtenerDocumentacionBloqueantePendienteQuery(_centroB), CancellationToken.None))
            .Should().BeEmpty("el Centro del Tenant B no existe para A");
        // Un Centro de A sin ningún bloqueo (sin gestión CAE) tampoco devuelve nada.
        (await handler.Handle(new ObtenerDocumentacionBloqueantePendienteQuery(_centroA3SinGestion), CancellationToken.None))
            .Should().BeEmpty();
    }

    /// <summary>
    /// La pantalla de la tolerancia por defecto del Cliente empresarial lee lo que el comando escribe y lo que la regla de acceso
    /// consume: sin fila es 0, y un Cliente empresarial no ve las tolerancias de otro (ni de otro Tenant).
    /// </summary>
    [Fact]
    public async Task La_pantalla_de_tolerancia_por_defecto_del_Cliente_empresarial_lee_la_misma_tolerancia_que_aplica_la_regla()
    {
        var comoA = Runtime(_tenantA);
        var handler = new ObtenerToleranciasClienteEmpresarialQueryHandler(comoA, new AlcanceDatosServiceFalso());

        var delCliente = await handler.Handle(new ObtenerToleranciasClienteEmpresarialQuery(_clienteA), CancellationToken.None);

        delCliente.Single(t => t.TipoDocumentoId == _tipoCertificado).ToleranciaDias.Should().Be(ToleranciaA4,
            "es la tolerancia que hereda el Centro A4 de su Cliente empresarial");
        delCliente.Single(t => t.TipoDocumentoId == _tipoPss).ToleranciaDias.Should().Be(0, "sin fila la tolerancia es 0");
        delCliente.Select(t => t.Ambito).Should().OnlyContain(a => a == AmbitoAplicacion.Trabajador || a == AmbitoAplicacion.Empresa,
            "solo los ámbitos que la regla de acceso evalúa");

        // Un Cliente empresarial fuera del alcance del usuario no enseña nada.
        var sinAcceso = new ObtenerToleranciasClienteEmpresarialQueryHandler(comoA, new AlcanceDatosServiceFalso(clienteIds: [Guid.NewGuid()]));
        (await sinAcceso.Handle(new ObtenerToleranciasClienteEmpresarialQuery(_clienteA), CancellationToken.None)).Should().BeEmpty();

        // Y bajo RLS: el Cliente empresarial de B no tiene filas para A.
        var delOtroTenant = await handler.Handle(new ObtenerToleranciasClienteEmpresarialQuery(_clienteB), CancellationToken.None);
        delOtroTenant.Should().OnlyContain(t => t.ToleranciaDias == 0, "la tolerancia 100 de B no se ve desde A");
    }

    [Fact]
    public async Task R2_con_alcance_limitado_solo_ve_su_Centro_y_el_bloqueo_se_decide_con_las_condiciones_de_ese_Centro()
    {
        // Un Gestor CAE que ve solo el Centro A2 (tolerancia 15). El alcance limita lo que se devuelve; el bloqueo es el
        // de A2, no el de A1 (tolerancia 0): la Empresa vencida hace 1 dia esta bloqueada en A1 pero no aqui.
        var pendientes = await MiTrabajo(Runtime(_tenantA), new AlcanceDatosServiceFalso(centroIds: [_centroA2]));

        pendientes.Should().NotBeEmpty();
        pendientes.Select(p => p.CentroId).Distinct().Should().Equal([_centroA2], "solo el Centro visible");

        var vencida16 = _casos.Single(c => c.Nombre == "R2 · certificado de Empresa vencido hace 16 dias");
        pendientes.Should().ContainSingle(p => p.TrabajadorId == vencida16.TrabajadorId && p.Ambito == AmbitoAplicacion.Empresa
            && p.Situacion == SituacionDeRequisitoBloqueante.Vencido && p.ToleranciaDias == ToleranciaA2);

        var vencida1 = _casos.Single(c => c.Nombre == "R2 · certificado de Empresa vencido hace 1 dia");
        pendientes.Should().NotContain(p => p.TrabajadorId == vencida1.TrabajadorId,
            "dentro de la tolerancia de A2 (15 dias); que en A1 (tolerancia 0) este bloqueada no le alcanza aqui");

        // Y no se cuela el requisito de Trabajador marcado en otros Centros.
        pendientes.Should().NotContain(p => p.Ambito == AmbitoAplicacion.Trabajador);
    }

    [Fact]
    public async Task La_pantalla_de_requisitos_del_Centro_ensena_la_tolerancia_propia_y_la_heredada_de_su_Cliente_empresarial()
    {
        // Lo que el Gestor CAE ve al editar: la personalizada del Centro (null = hereda) y la que heredaria del Cliente
        // empresarial TITULAR DE ESE CENTRO. Cada Centro lee la de su Cliente empresarial, no la de otro.
        var comoA = Runtime(_tenantA);

        async Task<DocumentacionRequeridaCentroDto> Fila(Guid centro) =>
            (await new ObtenerDocumentacionRequeridaDeCentroQueryHandler(comoA, comoA, new AlcanceDatosServiceFalso())
                .Handle(new ObtenerDocumentacionRequeridaDeCentroQuery(centro), CancellationToken.None))!
                .Single(d => d.TipoDocumentoId == _tipoCertificado);

        var a1 = await Fila(_centroA1);
        (a1.ToleranciaDias, a1.ToleranciaHeredadaDias).Should().Be((ToleranciaA1, ToleranciaA4));
        var a2 = await Fila(_centroA2);
        (a2.ToleranciaDias, a2.ToleranciaHeredadaDias).Should().Be((ToleranciaA2, ToleranciaA4));
        var a4 = await Fila(_centroA4);
        (a4.ToleranciaDias, a4.ToleranciaHeredadaDias).Should().Be(((int?)null, ToleranciaA4));
        var a6 = await Fila(_centroA6);
        (a6.ToleranciaDias, a6.ToleranciaHeredadaDias).Should().Be(((int?)null, ToleranciaA6));
        var a7 = await Fila(_centroA7);
        (a7.ToleranciaDias, a7.ToleranciaHeredadaDias).Should().Be(((int?)null, ToleranciaA7));
    }

    [Fact]
    public async Task Aislamiento_cada_Tenant_ve_solo_lo_suyo_y_un_requisito_de_otro_Tenant_no_bloquea_a_nadie()
    {
        // B: su Trabajador esta bloqueado por el certificado de su Empresa y solo B lo ve; rige la tolerancia de SU
        // Cliente empresarial (100), no la del Tenant A (30).
        var deB = await MiTrabajo(Runtime(_tenantB));
        var filaB = deB.Should().ContainSingle().Subject;
        filaB.TrabajadorId.Should().Be(_trabajadorB);
        filaB.CentroId.Should().Be(_centroB);
        filaB.Ambito.Should().Be(AmbitoAplicacion.Empresa);
        filaB.ToleranciaDias.Should().Be(100);

        // C: el tipo es bloqueante en B pero no en C; el Trabajador de C, cuya Empresa tampoco tiene el certificado,
        // no esta bloqueado. Si la designacion cruzara Tenants, apareceria aqui.
        (await MiTrabajo(Runtime(_tenantC))).Should().BeEmpty(
            "el requisito de Empresa que es bloqueante en el Tenant B no alcanza a los Trabajadores del Tenant C");

        // A: no ve nada de B ni de C.
        var deA = await MiTrabajo(Runtime(_tenantA));
        deA.Should().NotContain(p => p.TrabajadorId == _trabajadorB || p.TrabajadorId == _trabajadorC);
        deA.Should().NotContain(p => p.CentroId == _centroB || p.CentroId == _centroC);
        deA.Should().NotBeEmpty("control: A si tiene bloqueos propios");
    }

    [Fact]
    public async Task Un_tipo_de_Empresa_marcado_solo_en_un_Centro_sin_gestion_CAE_no_declara_requisito()
    {
        // La marca de un Centro sin gestion CAE no cuenta (ese Centro no exige nada): no se extiende a los demas Centros.
        var comoD = Runtime(_tenantD);

        // Controles positivos bajo RLS: la marca existe y el Trabajador esta asignado a un Centro con gestion CAE. Sin
        // ellos, «vacio» pasaria tambien si la siembra no fuera visible.
        (await comoD.TiposDocumentoCentros.CountAsync(x => x.CentroId == _centroDSinGestion && x.Incluido && x.BloqueaAcceso)).Should().Be(1);
        (await comoD.Asignaciones.CountAsync(a => a.TrabajadorId == _trabajadorD && a.FechaBaja == null)).Should().Be(1);

        (await MiTrabajo(comoD)).Should().BeEmpty(
            "el unico Centro que marca el certificado como bloqueante no tiene gestion CAE");
    }

    private string Nombre(Guid centro) =>
        centro == _centroA1 ? "A1" : centro == _centroA2 ? "A2" : centro == _centroA3SinGestion ? "A3 (sin gestion CAE)"
        : centro == _centroA4 ? "A4" : centro == _centroA5 ? "A5" : centro == _centroA6 ? "A6" : centro == _centroA7 ? "A7" : centro.ToString();

    private static string Describir(IEnumerable<DocumentacionBloqueantePendienteDto> filas) =>
        "[" + string.Join("; ", filas.Select(f => $"{f.Ambito}/{f.Situacion}/tolerancia={f.ToleranciaDias}")) + "]";

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class TenantActualDeLaPeticion(Guid tenantDeLaSesion) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantDeLaSesion;
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
