using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using FluentAssertions;

namespace CaeManager.Application.Tests.Tenants.Encargo;

/// <summary>
/// El techo del rol efectivo por Encargo de administración (decisión D-8, 2026-10-08), como matriz
/// completa: rol de cartera × perfil de Propiedad en el Tenant de origen × encargo × rol de sesión.
/// La elevación exige las cuatro cosas a la vez; cualquier otra combinación deja el rol de la
/// cartera, y sin cartera no hay rol.
/// </summary>
public class TechoDeRolPorEncargoTests
{
    private static readonly Guid Usuario = Guid.NewGuid();
    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly Guid Propietario = Guid.NewGuid();
    private static readonly DateTime Ahora = new(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc);

    public enum Encargo
    {
        Ninguno,
        Vigente,
        Retirado,
        Caducado,
        /// <summary>Misma operación y mismo Tenant propietario, pero la fila nombra a otro Operador CAE.</summary>
        DeOtroOperadorCae,
        /// <summary>Misma operación y mismo Operador CAE, pero la fila nombra a otro Tenant propietario.</summary>
        DeOtroTenantPropietario,
        /// <summary>Mismo Operador CAE y mismo Tenant propietario, ligado a otra operación.</summary>
        DeOtraOperacion,
    }

    private static readonly string?[] RolesDeCartera = [null, "Consulta", "GestorCae", "CoordinadorCae"];
    private static readonly string?[] Perfiles = [null, "Administrador", "DireccionCae"];

    public static TheoryData<string?, string?, Encargo, bool> Matriz()
    {
        var datos = new TheoryData<string?, string?, Encargo, bool>();
        foreach (var rol in RolesDeCartera)
            foreach (var perfil in Perfiles)
                foreach (var encargo in Enum.GetValues<Encargo>())
                    foreach (var sesionCoincide in new[] { true, false })
                        datos.Add(rol, perfil, encargo, sesionCoincide);
        return datos;
    }

    [Theory]
    [MemberData(nameof(Matriz))]
    public async Task El_rol_solo_sube_con_cartera_de_gestion_encargo_vigente_perfil_de_Propiedad_y_sesion_coincidente(
        string? rolDeCartera, string? perfil, Encargo encargo, bool sesionCoincide)
    {
        var escenario = new Escenario(rolDeCartera, perfil, encargo);

        var resultado = await escenario.ResolverAsync(sesionCoincide ? perfil : "GestorCae");

        var eleva = rolDeCartera is "GestorCae" or "CoordinadorCae"
                    && encargo == Encargo.Vigente
                    && perfil is not null
                    && sesionCoincide;

        if (rolDeCartera is null)
            resultado.Should().Be(RolEfectivoPorOperacion.Ninguno, "sin cartera vigente no hay rol, haya o no encargo");
        else if (eleva)
            resultado.Should().Be(new RolEfectivoPorOperacion(perfil, escenario.EncargoId),
                "con las cuatro condiciones el rol sube al perfil de origen y queda señalado el encargo que lo ampara");
        else
            resultado.Should().Be(new RolEfectivoPorOperacion(rolDeCartera, null),
                "si falta una sola condición manda la cartera y no se señala ningún encargo");
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    public async Task Un_perfil_de_origen_que_no_es_de_Propiedad_no_eleva(string perfilNoDePropiedad)
    {
        var escenario = new Escenario("GestorCae", perfilNoDePropiedad, Encargo.Vigente);

        (await escenario.ResolverAsync(perfilNoDePropiedad))
            .Should().Be(new RolEfectivoPorOperacion("GestorCae", null));
    }

    [Theory]
    [InlineData(Encargo.Ninguno)]
    [InlineData(Encargo.Retirado)]
    [InlineData(Encargo.Caducado)]
    [InlineData(Encargo.DeOtroOperadorCae)]
    public async Task Sin_encargo_vigente_ni_siquiera_se_lee_el_rol_de_la_cuenta_en_su_Tenant_de_origen(Encargo encargo)
    {
        var escenario = new Escenario("CoordinadorCae", "Administrador", encargo);

        await escenario.ResolverAsync("Administrador");

        escenario.VecesQueSeLeyoElPerfil.Should().Be(0,
            "el rol de origen solo se pregunta después de comprobar cartera y encargo: es lo que justifica su excepción en el trinquete");
    }

    [Fact]
    public async Task Sin_cartera_tampoco_se_lee_el_rol_de_origen_aunque_haya_encargo()
    {
        var escenario = new Escenario(rolDeCartera: null, "Administrador", Encargo.Vigente);

        (await escenario.ResolverAsync("Administrador")).Should().Be(RolEfectivoPorOperacion.Ninguno);
        escenario.VecesQueSeLeyoElPerfil.Should().Be(0);
    }

    [Fact]
    public async Task Un_encargo_con_fecha_de_fin_eleva_hasta_el_instante_anterior_y_no_en_el_de_fin()
    {
        var escenario = new Escenario("GestorCae", "Administrador", Encargo.Caducado);

        (await escenario.ResolverAsync("Administrador", Ahora.AddTicks(-1)))
            .Should().Be(new RolEfectivoPorOperacion("Administrador", escenario.EncargoId), "vigencia semiabierta: [desde, hasta)");
        (await escenario.ResolverAsync("Administrador", Ahora))
            .Should().Be(new RolEfectivoPorOperacion("GestorCae", null));
    }

    private sealed class Escenario : IOperacionesQueryContext, IEncargosAdministracionQueryContext, IPerfilDePropiedadEnOrigen
    {
        private readonly AsignacionOperacion _operacion;
        private readonly List<AsignacionOperacion> _operaciones = [];
        private readonly List<AsignacionCartera> _carteras = [];
        private readonly List<EncargoAdministracion> _encargos = [];
        private readonly string? _perfil;

        public Escenario(string? rolDeCartera, string? perfil, Encargo encargo)
        {
            _perfil = perfil;
            _operacion = OperacionVigente();
            _operaciones.Add(_operacion);

            if (rolDeCartera is not null)
            {
                var cartera = AsignacionCartera.Externa(
                    _operacion, Usuario, rolDeCartera, AmbitoAsignacion.Universal, Ahora.AddDays(-20), null, Ahora.AddDays(-20));
                Forzar(cartera, nameof(AsignacionCartera.Estado), EstadoAsignacion.Vigente);
                _carteras.Add(cartera);
            }

            if (Construir(encargo) is { } fila)
            {
                _encargos.Add(fila);
                EncargoId = fila.Id;
            }
        }

        public Guid? EncargoId { get; }
        public int VecesQueSeLeyoElPerfil { get; private set; }

        public IQueryable<AsignacionOperacion> AsignacionesOperacion =>
            new TestAsyncQueryable<AsignacionOperacion>(_operaciones.AsQueryable());

        public IQueryable<AsignacionCartera> AsignacionesCartera =>
            new TestAsyncQueryable<AsignacionCartera>(_carteras.AsQueryable());

        public IQueryable<EncargoAdministracion> EncargosAdministracion =>
            new TestAsyncQueryable<EncargoAdministracion>(_encargos.AsQueryable());

        public Task<string?> ObtenerAsync(Guid usuarioId, Guid tenantOrigenId, CancellationToken cancellationToken = default)
        {
            VecesQueSeLeyoElPerfil++;
            usuarioId.Should().Be(Usuario);
            tenantOrigenId.Should().Be(Operador);
            return Task.FromResult(_perfil);
        }

        public Task<RolEfectivoPorOperacion> ResolverAsync(string? rolDeSesionEnOrigen, DateTime? ahora = null) =>
            new TechoDeRolPorEncargo(this, this, this).ResolverAsync(
                Usuario, Operador, Propietario, _operacion.Id, rolDeSesionEnOrigen, ahora ?? Ahora, default);

        private EncargoAdministracion? Construir(Encargo encargo)
        {
            switch (encargo)
            {
                case Encargo.Ninguno:
                    return null;
                case Encargo.Vigente:
                    return Registrar(_operacion);
                case Encargo.Retirado:
                    var retirado = Registrar(_operacion);
                    retirado.Retirar(Guid.NewGuid(), Ahora.AddDays(-1));
                    return retirado;
                case Encargo.Caducado:
                    return Registrar(_operacion, vigenciaHasta: Ahora);
                case Encargo.DeOtroOperadorCae:
                    var deOtroOperador = Registrar(_operacion);
                    Forzar(deOtroOperador, nameof(EncargoAdministracion.OperadorTenantId), Guid.NewGuid());
                    return deOtroOperador;
                case Encargo.DeOtroTenantPropietario:
                    var deOtroPropietario = Registrar(_operacion);
                    Forzar(deOtroPropietario, nameof(EncargoAdministracion.PropietarioTenantId), Guid.NewGuid());
                    return deOtroPropietario;
                case Encargo.DeOtraOperacion:
                    var otra = OperacionVigente();
                    _operaciones.Add(otra);
                    return Registrar(otra);
                default:
                    throw new ArgumentOutOfRangeException(nameof(encargo));
            }
        }

        private static AsignacionOperacion OperacionVigente()
        {
            var operacion = AsignacionOperacion.Externa(
                Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora.AddDays(-30), null, Ahora.AddDays(-30));
            Forzar(operacion, nameof(AsignacionOperacion.Estado), EstadoAsignacion.Vigente);
            return operacion;
        }

        private static EncargoAdministracion Registrar(AsignacionOperacion operacion, DateTime? vigenciaHasta = null) =>
            EncargoAdministracion.Registrar(
                operacion, "Cláusula 7.2 del contrato de servicios", EncargoAdministracion.VersionTextoVigente,
                OrigenEncargoAdministracion.AdministradorPropio, Guid.NewGuid(), Ahora.AddDays(-10), vigenciaHasta);

        /// <summary>
        /// Fila que el dominio no deja construir (un encargo cuyo Operador CAE o Tenant propietario
        /// no son los de su operación): es justo lo que el techo tiene que seguir comprobando contra
        /// la fila en vez de darlo por bueno.
        /// </summary>
        private static void Forzar(object entidad, string propiedad, object valor) =>
            entidad.GetType().GetProperty(propiedad)!.SetValue(entidad, valor);
    }
}
