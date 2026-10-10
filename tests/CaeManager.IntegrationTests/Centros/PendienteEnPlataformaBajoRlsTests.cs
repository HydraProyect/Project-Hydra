using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Centros;

/// <summary>
/// «Pendiente en la plataforma» (decisión del propietario del 2026-10-10) bajo RLS real, leyendo <b>como
/// <c>cae_app_runtime</c></b> con los interceptores de sellado y de sesión RLS de producción (mismo arnés que
/// <c>CumplimientoCentroDocumentosDuplicadosBajoRlsTests</c>). Un documento que vale y que en la plataforma CAE del Centro
/// está sin subir o subido sin validar pone el Centro en <see cref="EstadoCentro.Pendiente"/>, sale en su propio grupo del
/// desglose y baja el porcentaje del Centro. Bloquea a personas en ese Centro <b>solo si el Centro marca el tipo con
/// <c>BloqueaAcceso</c></b> (corrección del propietario del 2026-10-10): al Trabajador del documento, o a todos los de la
/// Empresa si el documento es de Empresa; un RNT bloqueante pendiente bloquea, una ISO opcional pendiente no. No cuentan:
/// otro Tenant propietario (RLS), un Trabajador con la Asignación de baja, un Trabajador que no está asignado a ese Centro,
/// una acreditación validada, ni un Centro fuera del alcance de quien consulta. Un Centro sin plataforma no cambia. La
/// siembra va como propietario de la base, porque no es lo que se mide.
/// </summary>
public class PendienteEnPlataformaBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private CaeManagerDbContext _propietario = null!;
    private CaeManagerDbContext _runtime = null!;
    private Guid _tenantSesion;
    private Escenario _sesion = null!;
    private Escenario _ajeno = null!;

    private sealed record Escenario(
        Guid CentroConPlataforma, Guid CentroSinPlataforma, Guid OtroCentro,
        Guid EmpresaPropia, Guid Ana, Guid Beto, Guid Bajo, Guid Dani, Guid Eva,
        Guid TipoTrabajador, Guid TipoEmpresa, Guid TipoIso, Guid AcreditacionDeAna);

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var tenantSesion = new Tenant("Tenant propietario de la sesión");
        var tenantAjeno = new Tenant("Tenant propietario ajeno");
        _propietario.Tenants.AddRange(tenantSesion, tenantAjeno);
        await _propietario.SaveChangesAsync();
        _tenantSesion = tenantSesion.Id;

        _sesion = await SembrarAsync(tenantSesion.Id);
        _ajeno = await SembrarAsync(tenantAjeno.Id);

        var tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantSesion);
        var usuario = new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: _tenantSesion);
        var opcionesRuntime = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(tenantDeLaPeticion, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        _runtime = new CaeManagerDbContext(opcionesRuntime, new EphemeralDataProtectionProvider(), tenantDeLaPeticion);
    }

    public async Task DisposeAsync()
    {
        await _runtime.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    private CalculoEstadoCentroService Calculo() => new(_runtime, _runtime, _runtime, _runtime, _runtime, _runtime);

    private EvaluacionDeAccesoPorCentroService Evaluacion(IAlcanceDatosService? alcance = null) =>
        new(_runtime, _runtime, _runtime, _runtime, _runtime, alcance ?? new AlcanceDatosServiceFalso());

    /// <summary>Control del instrumento: la conexión de lectura está de verdad bajo RLS.</summary>
    [Fact]
    public async Task La_lectura_va_bajo_RLS_efectiva()
    {
        (await _runtime.AcreditacionesDocumentoPlataforma.IgnoreQueryFilters().CountAsync(a => a.Id == _ajeno.AcreditacionDeAna)).Should().Be(0,
            "sin filtro global de EF, solo RLS puede ocultar la acreditación del Tenant ajeno");
        (await _runtime.AcreditacionesDocumentoPlataforma.IgnoreQueryFilters().CountAsync(a => a.Id == _sesion.AcreditacionDeAna)).Should().Be(1,
            "control positivo: la del Tenant de la sesión sí se ve");
    }

    [Fact]
    public async Task El_Centro_con_plataforma_queda_Pendiente_y_el_Centro_sin_plataforma_no_cambia()
    {
        var estados = await Calculo().CalcularAsync(
            [_sesion.CentroConPlataforma, _sesion.CentroSinPlataforma, _sesion.OtroCentro, _ajeno.CentroConPlataforma], CancellationToken.None);

        estados[_sesion.CentroConPlataforma].Estado.Should().Be(EstadoCentro.Pendiente);
        estados[_sesion.CentroSinPlataforma].Estado.Should().Be(EstadoCentro.Vigente, "sin plataforma no hay Pendiente");
        estados[_sesion.OtroCentro].Estado.Should().Be(EstadoCentro.Vigente,
            "la acreditación pendiente de Dani es del acceso del otro Centro, no de este");
        // El servicio devuelve una entrada por cada id pedido, aunque no lo vea. El Centro del otro Tenant tiene el mismo
        // escenario y, sin RLS, saldría Pendiente con sus dos causas: bajo RLS no se ve nada suyo.
        estados[_ajeno.CentroConPlataforma].Estado.Should().Be(EstadoCentro.Vigente, "RLS: nada del otro Tenant cuenta para esta sesión");
        estados[_ajeno.CentroConPlataforma].Causas.Should().BeEmpty();

        var pendientes = estados[_sesion.CentroConPlataforma].Causas.Where(c => c.PendienteEnPlataforma).ToList();
        pendientes.Should().HaveCount(3,
            "Ana (sin subir), el RNT de la Empresa (subido sin validar) y la ISO opcional (sin subir): la marca de bloqueo no cambia el estado del Centro; ni Beto (validado), ni Bajo (de baja), ni Dani (no asignado)");
        pendientes.Should().ContainSingle(c => c.TrabajadorId == _sesion.Ana && c.Ambito == AmbitoCausa.Trabajador)
            .Which.Descripcion.Should().Be("Formación 60h — Ana Simpson — sin subir a la plataforma", "el motivo nombra el documento y su estado");
        pendientes.Should().ContainSingle(c => c.TipoDocumentoId == _sesion.TipoEmpresa && c.Ambito == AmbitoCausa.Empresa)
            .Which.Descripcion.Should().Be("RNT — Empresa — subido, sin validar en la plataforma");
        pendientes.Should().ContainSingle(c => c.TipoDocumentoId == _sesion.TipoIso && c.Ambito == AmbitoCausa.Empresa)
            .Which.Descripcion.Should().Be("ISO 9001 — Empresa — sin subir a la plataforma");
        pendientes.Should().OnlyContain(c => c.Estado == null && !c.Bloqueante,
            "el Pendiente no describe la vigencia del documento ni pone el Centro en Bloqueado");
    }

    [Fact]
    public async Task El_desglose_lleva_los_pendientes_en_su_propio_grupo()
    {
        var handler = new ObtenerCentrosQueryHandler(_runtime, _runtime, new AlcanceDatosServiceFalso(), Calculo());

        var resultado = await handler.Handle(new ObtenerCentrosQuery(null, null, CentroId: _sesion.CentroConPlataforma), CancellationToken.None);

        var centro = resultado.Elementos.Should().ContainSingle().Subject;
        centro.Estado.Should().Be(EstadoCentro.Pendiente);
        centro.Recuentos.TotalPendientes.Should().Be(3);
        centro.Recuentos.TotalVencidas.Should().Be(0, "un pendiente no es un vencido");
        centro.Recuentos.TotalProximas.Should().Be(0, "ni algo por vencer");
    }

    [Fact]
    public async Task El_porcentaje_del_Centro_con_plataforma_baja_y_el_del_Centro_sin_plataforma_no()
    {
        var cumplimiento = await Calculo().CalcularCumplimientoAsync(
            [_sesion.CentroConPlataforma, _sesion.CentroSinPlataforma], CancellationToken.None);

        cumplimiento[_sesion.CentroConPlataforma].Requeridos.Should().Be(3, "Ana, Beto y Eva tienen el tipo exigido en este Centro; Bajo está de baja");
        cumplimiento[_sesion.CentroConPlataforma].AlDia.Should().Be(2, "el de Ana vale pero está sin subir a la plataforma del Centro");
        cumplimiento[_sesion.CentroSinPlataforma].Should().Be(new FraccionCumplimiento(1, 1),
            "el mismo documento de Ana está al día en un Centro sin plataforma");
    }

    [Fact]
    public async Task El_pendiente_de_tipo_bloqueante_bloquea_a_la_persona_en_ese_Centro_y_el_de_Empresa_a_todos_sus_Trabajadores_asignados()
    {
        var evaluacion = await Evaluacion().EvaluarAsync(null, CancellationToken.None);

        var bloqueos = evaluacion.Requisitos.Where(r => ReglaBloqueoDeAcceso.Bloquea(r.Resultado.Situacion)).ToList();
        bloqueos.Should().HaveCount(4, "Ana por su Formación y Ana, Beto y Eva por el RNT de su Empresa");
        bloqueos.Where(b => b.CentroId != _sesion.CentroConPlataforma).Should().BeEmpty(
            "ni el Centro sin plataforma, ni el otro Centro, ni nada del Tenant ajeno (RLS) bloquean a nadie");
        bloqueos.Where(b => b.ToleranciaDias != 0).Should().BeEmpty("el Pendiente no tiene tolerancia");

        bloqueos.Should().ContainSingle(b => b.TrabajadorId == _sesion.Ana && b.Ambito == AmbitoAplicacion.Trabajador,
                "el documento de Ana está sin subir: bloquea a Ana, y solo a Ana, por su documento")
            .Which.Resultado.Situacion.Should().Be(SituacionDeRequisitoBloqueante.PendienteDeSubirAPlataforma);
        var deEmpresa = bloqueos.Where(b => b.Ambito == AmbitoAplicacion.Empresa).ToList();
        deEmpresa.Select(b => b.TrabajadorId).Should().BeEquivalentTo([_sesion.Ana, _sesion.Beto, _sesion.Eva],
            "el RNT de la Empresa está sin validar: bloquea a todos sus Trabajadores asignados a este Centro, no a Bajo (de baja) ni a Dani (de otro Centro)");
        deEmpresa.Where(b => b.EmpresaId != _sesion.EmpresaPropia || b.TipoDocumentoId != _sesion.TipoEmpresa
            || b.Resultado.Situacion != SituacionDeRequisitoBloqueante.SinValidarEnPlataforma).Should().BeEmpty();
    }

    [Fact]
    public async Task Una_ISO_opcional_pendiente_no_bloquea_a_nadie_porque_el_Centro_no_la_marca_bloqueante()
    {
        var evaluacion = await Evaluacion().EvaluarAsync(null, CancellationToken.None);

        evaluacion.Requisitos.Where(r => r.TipoDocumentoId == _sesion.TipoIso).Should().BeEmpty(
            "la ISO está incluida en el Centro pero sin BloqueaAcceso: está pendiente en la plataforma y no bloquea a nadie");
        evaluacion.Requisitos.Where(r => r.TipoDocumentoId == _sesion.TipoEmpresa && ReglaBloqueoDeAcceso.Bloquea(r.Resultado.Situacion))
            .Should().NotBeEmpty("control positivo: el RNT, mismo ámbito y mismo Centro pero bloqueante, sí bloquea");
    }

    [Fact]
    public async Task Al_quitar_la_marca_de_bloqueo_el_mismo_pendiente_deja_de_bloquear_y_el_Centro_sigue_Pendiente()
    {
        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            var fila = await _propietario.TiposDocumentoCentros
                .SingleAsync(tc => tc.TipoDocumentoId == _sesion.TipoTrabajador && tc.CentroId == _sesion.CentroConPlataforma);
            fila.Actualizar(fila.Incluido, fila.PeriodicidadEspecialMeses, bloqueaAcceso: false, fila.ArchivoUrl, fila.NombreArchivoOriginal, fila.ToleranciaDias);
            await _propietario.SaveChangesAsync();
        }

        var evaluacion = await Evaluacion().EvaluarAsync(null, CancellationToken.None);
        evaluacion.Requisitos.Where(r => r.TipoDocumentoId == _sesion.TipoTrabajador && r.CentroId == _sesion.CentroConPlataforma)
            .Should().BeEmpty("sin la marca no hay requisito bloqueante: la Formación de Ana, aún sin subir, no la bloquea");
        evaluacion.Requisitos.Where(r => r.TipoDocumentoId == _sesion.TipoEmpresa && r.CentroId == _sesion.CentroConPlataforma
            && ReglaBloqueoDeAcceso.Bloquea(r.Resultado.Situacion)).Should().HaveCount(3, "control positivo: el RNT, con su marca, sigue bloqueando");

        var estado = (await Calculo().CalcularAsync([_sesion.CentroConPlataforma], CancellationToken.None))[_sesion.CentroConPlataforma];
        estado.Estado.Should().Be(EstadoCentro.Pendiente, "la marca de bloqueo no cambia el estado del Centro");
        estado.Causas.Should().Contain(c => c.PendienteEnPlataforma && c.TrabajadorId == _sesion.Ana);
    }

    [Fact]
    public async Task Fuera_del_alcance_de_quien_consulta_no_hay_bloqueo_por_pendiente()
    {
        var evaluacion = await Evaluacion(new AlcanceDatosServiceFalso(centroIds: [_sesion.OtroCentro])).EvaluarAsync(null, CancellationToken.None);

        evaluacion.Requisitos.Should().NotContain(r => r.CentroId == _sesion.CentroConPlataforma,
            "el Centro con el pendiente no es visible para este usuario");
    }

    /// <summary>
    /// En un Tenant: un Centro con acceso de plataforma (P), un Centro sin plataforma (S) y otro Centro con su propio acceso
    /// (Q). Tres tipos: «Formación 60h» (Trabajador) y «RNT» (Empresa), requeridos por defecto y marcados «bloquea el acceso»
    /// en P, S y Q; e «ISO 9001» (Empresa), no requerida por defecto, incluida en P sin marca de bloqueo. Ana (asignada a P y
    /// S): Formación vigente sin subir en P. Beto (asignado a P): validado en P. Eva (asignada a P): validado en P. Bajo: su
    /// Asignación a P está de baja y su documento sigue subido sin validar en P. Dani (asignado a Q): su documento tiene una
    /// acreditación sin subir en el acceso de P, aunque no trabaja allí, y validada en Q. La Empresa: RNT vigente subido sin
    /// validar en P y validado en Q; ISO vigente sin subir en P.
    /// </summary>
    private async Task<Escenario> SembrarAsync(Guid tenantId)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        var hoy = DiaDeNegocio.Hoy();

        var cliente = Empresa.CrearComoCliente("Cervezas Duff Ibérica", "B12345674", false, null, null);
        var empresa = new Empresa("Montajes Springfield S.L.", "B87654323");
        _propietario.Empresas.AddRange(cliente, empresa);
        _propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
        var tipoTrabajador = new TipoDocumento("Formación 60h", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        var tipoEmpresa = new TipoDocumento("RNT", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Empresa, requerido: RequisitoDocumental.Si);
        var tipoIso = new TipoDocumento("ISO 9001", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Empresa, requerido: RequisitoDocumental.No);
        _propietario.TiposDocumento.AddRange(tipoTrabajador, tipoEmpresa, tipoIso);
        var proveedor = new ProveedorPlataformaCae($"PLAT-{Guid.NewGuid():N}"[..14], "Plataforma de prueba");
        _propietario.ProveedoresPlataformaCae.Add(proveedor);
        await _propietario.SaveChangesAsync();

        var conPlataforma = new Centro(cliente.Id, empresa.Id, "Fábrica de Springfield");
        var sinPlataforma = new Centro(cliente.Id, empresa.Id, "Almacén de Shelbyville");
        var otro = new Centro(cliente.Id, empresa.Id, "Central nuclear");
        var ana = Trabajador.DeEmpresa(empresa.Id, "Ana", "Simpson", "77189989B");
        var beto = Trabajador.DeEmpresa(empresa.Id, "Beto", "Flanders", "12345678Z");
        var eva = Trabajador.DeEmpresa(empresa.Id, "Eva", "Krabappel", "33333333P");
        var bajo = Trabajador.DeEmpresa(empresa.Id, "Bajo", "Gumble", "87654321X");
        var dani = Trabajador.DeEmpresa(empresa.Id, "Dani", "Szyslak", "11111111H");
        _propietario.Centros.AddRange(conPlataforma, sinPlataforma, otro);
        _propietario.Trabajadores.AddRange(ana, beto, eva, bajo, dani);
        await _propietario.SaveChangesAsync();

        var asignacionDeBajo = new Asignacion(bajo.Id, conPlataforma.Id, hoy.AddDays(-30));
        asignacionDeBajo.DarDeBaja(hoy.AddDays(-1));
        _propietario.Asignaciones.AddRange(
            new Asignacion(ana.Id, conPlataforma.Id, hoy),
            new Asignacion(ana.Id, sinPlataforma.Id, hoy),
            new Asignacion(beto.Id, conPlataforma.Id, hoy),
            new Asignacion(eva.Id, conPlataforma.Id, hoy),
            asignacionDeBajo,
            new Asignacion(dani.Id, otro.Id, hoy));

        Documento Vigente(Guid trabajadorId) =>
            Documento.DeTrabajador(trabajadorId, tipoTrabajador.Id, hoy.AddDays(-5), VigenciaDocumento.VenceEl(hoy.AddYears(1)));
        var docAna = Vigente(ana.Id);
        var docBeto = Vigente(beto.Id);
        var docEva = Vigente(eva.Id);
        var docBajo = Vigente(bajo.Id);
        var docDani = Vigente(dani.Id);
        var docEmpresa = Documento.DeEmpresa(empresa.Id, tipoEmpresa.Id, hoy.AddDays(-5), VigenciaDocumento.VenceEl(hoy.AddYears(1)));
        var docIso = Documento.DeEmpresa(empresa.Id, tipoIso.Id, hoy.AddDays(-5), VigenciaDocumento.VenceEl(hoy.AddYears(1)));
        _propietario.Documentos.AddRange(docAna, docBeto, docEva, docBajo, docDani, docEmpresa, docIso);

        // La marca de bloqueo de cada Centro: Formación y RNT bloquean en los tres; la ISO solo está incluida en P.
        foreach (var centro in new[] { conPlataforma, sinPlataforma, otro })
        {
            _propietario.TiposDocumentoCentros.AddRange(
                new TipoDocumentoCentro(tipoTrabajador.Id, centro.Id, bloqueaAcceso: true),
                new TipoDocumentoCentro(tipoEmpresa.Id, centro.Id, bloqueaAcceso: true));
        }
        _propietario.TiposDocumentoCentros.Add(new TipoDocumentoCentro(tipoIso.Id, conPlataforma.Id, bloqueaAcceso: false));

        var canalP = CanalGestionDocumental.DePlataforma(conPlataforma.Id, "Acceso de prueba", proveedor.Id, null, null, null);
        var canalQ = CanalGestionDocumental.DePlataforma(otro.Id, "Acceso de la central", proveedor.Id, null, null, null);
        _propietario.CanalesGestionDocumental.AddRange(canalP, canalQ);
        await _propietario.SaveChangesAsync();

        AcreditacionDocumentoPlataforma Sin(Documento documento, CanalGestionDocumental canal) => new(documento.Id, canal.Id);
        AcreditacionDocumentoPlataforma Subida(Documento documento, CanalGestionDocumental canal)
        {
            var acreditacion = Sin(documento, canal);
            acreditacion.MarcarSubida();
            return acreditacion;
        }
        AcreditacionDocumentoPlataforma Validada(Documento documento, CanalGestionDocumental canal)
        {
            var acreditacion = Subida(documento, canal);
            acreditacion.MarcarAceptada(VigenciaEnPlataforma.NoVenceAqui);
            return acreditacion;
        }

        var acreditacionDeAna = Sin(docAna, canalP);
        _propietario.AcreditacionesDocumentoPlataforma.AddRange(
            acreditacionDeAna,
            Validada(docBeto, canalP),
            Validada(docEva, canalP),
            Subida(docBajo, canalP),
            Sin(docDani, canalP),
            Validada(docDani, canalQ),
            Subida(docEmpresa, canalP),
            Validada(docEmpresa, canalQ),
            Sin(docIso, canalP));
        await _propietario.SaveChangesAsync();

        return new Escenario(
            conPlataforma.Id, sinPlataforma.Id, otro.Id, empresa.Id, ana.Id, beto.Id, bajo.Id, dani.Id, eva.Id,
            tipoTrabajador.Id, tipoEmpresa.Id, tipoIso.Id, acreditacionDeAna.Id);
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    /// <summary>Como el <c>TenantActual</c> real: primero el ámbito explícito, luego el Tenant de la sesión.</summary>
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
