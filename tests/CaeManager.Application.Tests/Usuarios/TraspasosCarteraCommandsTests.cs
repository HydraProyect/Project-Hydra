using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Operaciones.Traspasos;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Operaciones.IncorporacionCartera;
using CaeManager.Domain.Operaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Usuarios;

public class TraspasosCarteraCommandsTests
{
    private sealed class AusenciasFalsas : IAusenciasGestorCae
    {
        public List<AusenciaGestorCae> Datos { get; } = [];
        public Task<IReadOnlyList<AusenciaGestorCae>> ListarAbiertasAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AusenciaGestorCae>>(Datos.Where(a => a.CerradaEnUtc is null).ToList());
        public Task<AusenciaGestorCae?> ObtenerAsync(Guid id, CancellationToken ct) => Task.FromResult(Datos.FirstOrDefault(a => a.Id == id));
        public void Agregar(AusenciaGestorCae ausencia) => Datos.Add(ausencia);
    }

    private sealed class WriterFalso : ITraspasosCarteraWriter
    {
        public List<(Guid Origen, Guid Destino, Guid? Ausencia, bool Mantener, Guid? Tenant)> Traspasos { get; } = [];
        public List<(Guid Ausencia, Guid Operacion, Guid? Tenant)> Retiradas { get; } = [];
        public bool Resultado { get; set; } = true;
        public Task<bool> TraspasarAsync(Guid operacionId, Guid propietarioTenantId, Guid operadorTenantId, Guid origenUsuarioId,
            Guid destinoUsuarioId, Guid actorUsuarioId, bool mantenerApoyo, Guid? coberturaAusenciaId, CancellationToken ct)
        {
            Traspasos.Add((origenUsuarioId, destinoUsuarioId, coberturaAusenciaId, mantenerApoyo, AmbitoTenantExplicito.TenantIdActual));
            return Task.FromResult(Resultado);
        }
        public Task RetirarCoberturaAsync(Guid ausenciaId, Guid operacionId, Guid propietarioTenantId, Guid operadorTenantId, Guid actorUsuarioId, CancellationToken ct)
        { Retiradas.Add((ausenciaId, operacionId, AmbitoTenantExplicito.TenantIdActual)); return Task.CompletedTask; }
    }

    private sealed class Escenario
    {
        public Guid Operador { get; } = Guid.NewGuid();
        public Guid Actor { get; } = Guid.NewGuid();
        public Guid Origen { get; } = Guid.NewGuid();
        public Guid Destino { get; } = Guid.NewGuid();
        public Guid Propietario { get; } = Guid.NewGuid();
        public AsignacionOperacion Operacion { get; }
        public DirectorioRolesEnOrigen Usuarios { get; } = new();
        public DirectorioDestinosPorUsuario Directorio { get; } = new();
        public CatalogoIncorporacionCarteraFalso Catalogo { get; } = new();
        public AusenciasFalsas Ausencias { get; } = new();
        public WriterFalso Writer { get; } = new();
        public TransaccionDeComandoFalsa Transaccion { get; } = new();
        public BloqueoCarteraUsuarioFalso Bloqueo { get; } = new();
        public UnitOfWorkConAmbito Uow { get; } = new();
        public Escenario()
        {
            var ahora = DateTime.UtcNow;
            Operacion = AsignacionOperacion.Externa(Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora);
            Usuarios.Asignar(Actor, Operador, "Administrador");
            Usuarios.Asignar(Origen, Operador, "GestorCae");
            Usuarios.Asignar(Destino, Operador, "GestorCae");
            Directorio.Cuentas[Origen] = new(true, "GestorCae", null, false);
            Directorio.Cuentas[Destino] = new(true, "GestorCae", null, false);
            Catalogo.RegistrarAsignable(Operador, Operacion, "Empresa Uno");
            Catalogo.CarterasVivas.Add((Operador, new(Operacion.Id, Propietario, "Empresa Uno", Origen, "GestorCae", true, null)));
        }
        public TraspasosCarteraCommandsHandler Handler() => new(new CurrentUserServicePorAmbito(Actor, Operador, "Administrador"),
            Usuarios, Directorio, Catalogo, Writer, Ausencias, Transaccion, Bloqueo, Uow);
        public TraspasarCarteraCommand Comando(Guid? ausenciaId = null) => new(Origen, [new(Operacion.Id, Destino)], false, ausenciaId);
        public AusenciaGestorCae Ausencia(TipoAusenciaGestorCae tipo)
        {
            var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
            var ausencia = new AusenciaGestorCae(Origen, tipo, hoy, tipo == TipoAusenciaGestorCae.BajaDefinitiva ? null : hoy.AddDays(7), Actor, DateTime.UtcNow);
            Ausencias.Agregar(ausencia);
            return ausencia;
        }
    }

    [Theory]
    [InlineData(TipoAusenciaGestorCae.Vacaciones, true)]
    [InlineData(TipoAusenciaGestorCae.BajaTemporal, true)]
    [InlineData(TipoAusenciaGestorCae.BajaDefinitiva, false)]
    public async Task Cobertura_temporal_se_marca_y_permanente_no(TipoAusenciaGestorCae tipo, bool temporal)
    {
        var e = new Escenario(); var ausencia = e.Ausencia(tipo);
        var r = await e.Handler().Handle(e.Comando(ausencia.Id), default);
        r.EsExitoso.Should().BeTrue();
        e.Writer.Traspasos.Should().ContainSingle().Which.Should().Be((e.Origen, e.Destino, temporal ? ausencia.Id : (Guid?)null, false, (Guid?)e.Propietario));
        ausencia.Coberturas.Should().ContainSingle();
        e.Uow.TenantsAlGuardar.Should().Equal(e.Operador);
        e.Bloqueo.Exclusivos.Should().Equal(new[] { e.Origen, e.Destino }.Order());
    }

    [Fact]
    public async Task Traspaso_manual_no_se_convierte_en_cobertura()
    {
        var e = new Escenario();
        (await e.Handler().Handle(e.Comando(), default)).EsExitoso.Should().BeTrue();
        e.Writer.Traspasos.Single().Ausencia.Should().BeNull();
        e.Ausencias.Datos.Should().BeEmpty();
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    public async Task Sin_autoridad_no_lee_ni_escribe_carteras(string rol)
    {
        var e = new Escenario(); e.Usuarios.Asignar(e.Actor, e.Operador, rol);
        (await e.Handler().Handle(e.Comando(), default)).EsFallido.Should().BeTrue();
        e.Writer.Traspasos.Should().BeEmpty();
        e.Transaccion.Ejecutadas.Should().Be(0);
    }

    [Fact]
    public async Task Destinatario_de_otro_Operador_CAE_no_recibe_cartera()
    {
        var e = new Escenario(); e.Usuarios.Asignar(e.Destino, e.Operador, null);
        e.Usuarios.Asignar(e.Destino, Guid.NewGuid(), "GestorCae");
        (await e.Handler().Handle(e.Comando(), default)).EsFallido.Should().BeTrue();
        e.Writer.Traspasos.Should().BeEmpty();
    }

    [Fact]
    public async Task Destinatario_ausente_no_recibe_cobertura()
    {
        var e = new Escenario(); var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        e.Ausencias.Agregar(new(e.Destino, TipoAusenciaGestorCae.Vacaciones, hoy, hoy.AddDays(7), e.Actor, DateTime.UtcNow));
        (await e.Handler().Handle(e.Comando(), default)).EsFallido.Should().BeTrue();
        e.Writer.Traspasos.Should().BeEmpty();
    }

    [Fact]
    public async Task Cerrar_ausencia_solo_retira_sus_coberturas_con_el_Tenant_propietario_activo()
    {
        var e = new Escenario(); var ausencia = e.Ausencia(TipoAusenciaGestorCae.Vacaciones);
        ausencia.Cubrir(e.Operacion.Id, e.Propietario, e.Destino);
        (await e.Handler().Handle(new CerrarAusenciaGestorCaeCommand(e.Origen, ausencia.Id), default)).EsExitoso.Should().BeTrue();
        e.Writer.Retiradas.Should().Equal((ausencia.Id, e.Operacion.Id, (Guid?)e.Propietario));
        e.Uow.TenantsAlGuardar.Should().Equal(e.Operador);
        e.Writer.Traspasos.Should().BeEmpty();
        ausencia.CerradaEnUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Una_empresa_ajena_o_destino_sin_elegir_impide_confirmar()
    {
        var e = new Escenario();
        (await e.Handler().Handle(new TraspasarCarteraCommand(e.Origen, [new(Guid.NewGuid(), e.Destino)]), default)).EsFallido.Should().BeTrue();
        (await e.Handler().Handle(new TraspasarCarteraCommand(e.Origen, [new(e.Operacion.Id, Guid.Empty)]), default)).EsFallido.Should().BeTrue();
        e.Writer.Traspasos.Should().BeEmpty();
    }

    [Fact]
    public async Task Fallo_de_escritura_no_confirma_transaccion_ni_anota_cobertura()
    {
        var e = new Escenario(); var ausencia = e.Ausencia(TipoAusenciaGestorCae.Vacaciones); e.Writer.Resultado = false;
        (await e.Handler().Handle(e.Comando(ausencia.Id), default)).EsFallido.Should().BeTrue();
        e.Transaccion.Confirmadas.Should().Be(0);
        ausencia.Coberturas.Should().BeEmpty();
    }
}
