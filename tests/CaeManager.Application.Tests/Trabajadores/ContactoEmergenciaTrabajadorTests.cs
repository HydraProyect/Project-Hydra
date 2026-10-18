using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Trabajadores.Commands.EditarFichaTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Trabajadores;

/// <summary>
/// El contacto de emergencia del Trabajador (decisión de Chris, 2026-10-18): se escribe con la ficha
/// (<see cref="EditarFichaTrabajadorCommand"/>, mismo alcance de cartera, misma concurrencia y mismo rol con escritura) y
/// se lee en <see cref="ObtenerTrabajadorPorIdQuery"/> solo si el lector lee la nota interna: nunca el Usuario de Cliente.
/// </summary>
public class ContactoEmergenciaTrabajadorTests
{
    private static Trabajador Paula() =>
        Trabajador.DeEmpresa(Guid.NewGuid(), "Paula", "Campos", "12345678Z", telefono: "+34600111222");

    private static EditarFichaTrabajadorCommand Ficha(Trabajador t, ContactoEmergenciaTrabajador? contacto) =>
        new(t.Id, t.Version, null, "Electricista", null, null, null, null, null, null, null, null, null,
            ContactoEmergencia: contacto);

    private static (EditarFichaTrabajadorCommandHandler Handler, UnitOfWorkFalso UnitOfWork) Montar(
        Trabajador? trabajador, AlcanceDatosServiceFalso? alcance = null, IPoliticaNotaInterna? politica = null)
    {
        var repositorio = new TrabajadorRepositorioFalso();
        if (trabajador is not null) repositorio.Agregar(trabajador);
        var unitOfWork = new UnitOfWorkFalso();
        return (new EditarFichaTrabajadorCommandHandler(repositorio, new Empresas.DelegacionYOficioEmpresaTests.CatalogoFalso(), alcance ?? new AlcanceDatosServiceFalso(),
            politica ?? PoliticaNotaInternaPruebas.Con("GestorCae"), unitOfWork), unitOfWork);
    }

    // ── Dominio ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("+34 611 22 33 44", "+34611223344")]
    [InlineData("0034 (611) 22-33-44", "+34611223344")]
    [InlineData("611.22.33.44", "611223344")]
    public void Normaliza_el_telefono_como_el_del_trabajador(string escrito, string guardado)
    {
        var t = Paula();
        t.FijarContactoEmergencia("  Luis Campos (padre)  ", escrito);

        t.ContactoEmergenciaNombre.Should().Be("Luis Campos (padre)");
        t.ContactoEmergenciaTelefono.Should().Be(guardado);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("+1234567890123456")]
    [InlineData("611 22 33 4A")]
    [InlineData("+34+611223344")]
    public void Rechaza_un_telefono_sin_formato_y_no_toca_nada(string telefono)
    {
        var t = Paula();
        t.FijarContactoEmergencia("Luis", "611223344");

        var accion = () => t.FijarContactoEmergencia("Otro", telefono);

        accion.Should().Throw<ArgumentException>();
        t.ContactoEmergenciaNombre.Should().Be("Luis");
        t.ContactoEmergenciaTelefono.Should().Be("611223344");
    }

    [Fact]
    public void Vacio_borra_y_un_nombre_demasiado_largo_se_rechaza()
    {
        var t = Paula();
        t.FijarContactoEmergencia("Luis", "611223344");

        t.FijarContactoEmergencia("  ", null);
        t.ContactoEmergenciaNombre.Should().BeNull();
        t.ContactoEmergenciaTelefono.Should().BeNull();

        t.FijarContactoEmergencia(new string('a', Trabajador.LongitudMaximaContactoEmergenciaNombre), null);
        var accion = () => t.FijarContactoEmergencia(new string('a', Trabajador.LongitudMaximaContactoEmergenciaNombre + 1), null);
        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Anonimizar_lo_vacia_y_despues_no_se_admite()
    {
        var t = Paula();
        t.FijarContactoEmergencia("Luis", "611223344");

        t.Anonimizar(DateTime.UtcNow);

        t.ContactoEmergenciaNombre.Should().BeNull();
        t.ContactoEmergenciaTelefono.Should().BeNull();
        Trabajador.CamposDeDatosPersonales.Should().Contain([
            nameof(Trabajador.ContactoEmergenciaNombre), nameof(Trabajador.ContactoEmergenciaTelefono)]);
        var accion = () => t.FijarContactoEmergencia("Luis", "611223344");
        accion.Should().Throw<InvalidOperationException>();
    }

    // ── Escritura: EditarFichaTrabajadorCommand ───────────────────────────

    [Fact]
    public async Task La_ficha_guarda_el_contacto_de_emergencia()
    {
        var t = Paula();
        var (handler, unitOfWork) = Montar(t);

        var resultado = await handler.Handle(Ficha(t, new("Luis Campos", "+34 611 22 33 44")), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        t.ContactoEmergenciaNombre.Should().Be("Luis Campos");
        t.ContactoEmergenciaTelefono.Should().Be("+34611223344");
        t.Telefono.Should().Be("+34600111222", "el teléfono del propio Trabajador no es el de emergencia");
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Sin_contacto_en_la_orden_no_se_cambia_y_con_campos_vacios_se_borra()
    {
        var t = Paula();
        t.FijarContactoEmergencia("Luis", "611223344");
        var (handler, _) = Montar(t);

        (await handler.Handle(Ficha(t, null), CancellationToken.None)).EsExitoso.Should().BeTrue();
        t.ContactoEmergenciaNombre.Should().Be("Luis", "null significa que quien edita la ficha no toca el contacto");
        t.ContactoEmergenciaTelefono.Should().Be("611223344");

        (await handler.Handle(Ficha(t, new("", "")), CancellationToken.None)).EsExitoso.Should().BeTrue();
        t.ContactoEmergenciaNombre.Should().BeNull();
        t.ContactoEmergenciaTelefono.Should().BeNull();
    }

    /// <summary>
    /// Un Gestor CAE cuya Asignación de Cartera no alcanza al Trabajador recibe lo mismo que si no existiera, y el
    /// contacto no cambia: el mismo control de alcance que el resto de la ficha.
    /// </summary>
    [Fact]
    public async Task Fuera_de_la_cartera_no_guarda_y_responde_no_encontrado()
    {
        var t = Paula();
        t.FijarContactoEmergencia("Luis", "611223344");
        var (handler, unitOfWork) = Montar(t,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [Guid.NewGuid()]));

        var resultado = await handler.Handle(Ficha(t, new("Quien no lo alcanza", "699999999")), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Trabajador.NoEncontrado");
        t.ContactoEmergenciaNombre.Should().Be("Luis");
        t.ContactoEmergenciaTelefono.Should().Be("611223344");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Con_una_version_anterior_responde_conflicto_y_no_pisa_el_contacto()
    {
        var t = Paula();
        t.FijarContactoEmergencia("Luis", "611223344");
        var (handler, unitOfWork) = Montar(t);

        var resultado = await handler.Handle(Ficha(t, new("Otro", "699999999")) with { Version = Guid.NewGuid() },
            CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        t.ContactoEmergenciaNombre.Should().Be("Luis");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_trabajador_anonimizado_no_admite_contacto()
    {
        var t = Paula();
        t.Anonimizar(DateTime.UtcNow);
        var (handler, unitOfWork) = Montar(t);

        var resultado = await handler.Handle(Ficha(t, new("Luis", "611223344")), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Trabajador.Anonimizado");
        t.ContactoEmergenciaNombre.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public void El_validador_limita_el_nombre_y_exige_formato_de_telefono()
    {
        var validador = new EditarFichaTrabajadorCommandValidator();
        var t = Paula();

        validador.Validate(Ficha(t, null)).IsValid.Should().BeTrue();
        validador.Validate(Ficha(t, new(null, null))).IsValid.Should().BeTrue();
        validador.Validate(Ficha(t, new(new string('a', Trabajador.LongitudMaximaContactoEmergenciaNombre), "+34 611 22 33 44")))
            .IsValid.Should().BeTrue();
        validador.Validate(Ficha(t, new("  " + new string('a', Trabajador.LongitudMaximaContactoEmergenciaNombre) + "  ", null)))
            .IsValid.Should().BeTrue("la longitud se mide sobre el nombre recortado, como lo guarda el dominio");

        var resultado = validador.Validate(Ficha(t, new(new string('a', Trabajador.LongitudMaximaContactoEmergenciaNombre + 1), "llamar a Luis")));

        resultado.Errors.Select(e => e.PropertyName).Should().BeEquivalentTo(
            ["ContactoEmergencia.Nombre", "ContactoEmergencia.Telefono"]);
    }

    /// <summary>
    /// Defensa en el servidor: si quien guarda la ficha no puede leer el contacto (la misma política que la consulta),
    /// la orden lo trae pero el handler lo ignora como si fuera null; el resto de la ficha sí se guarda.
    /// </summary>
    [Theory]
    [InlineData("Cliente")]
    [InlineData("RolQueNoExiste")]
    [InlineData(null)]
    public async Task Quien_no_lee_el_contacto_no_lo_cambia_aunque_la_orden_lo_traiga(string? rol)
    {
        var t = Paula();
        t.FijarContactoEmergencia("Luis", "611223344");
        var (handler, unitOfWork) = Montar(t, politica: PoliticaNotaInternaPruebas.Con(rol));

        var resultado = await handler.Handle(Ficha(t, new("", "")), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        t.ContactoEmergenciaNombre.Should().Be("Luis");
        t.ContactoEmergenciaTelefono.Should().Be("611223344");
        t.Puesto.Should().Be("Electricista", "el resto de la ficha sí se guarda");
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task La_impersonacion_no_cambia_el_contacto()
    {
        var t = Paula();
        t.FijarContactoEmergencia("Luis", "611223344");
        var (handler, _) = Montar(t, politica: PoliticaNotaInternaPruebas.Con("GestorCae",
            PoliticaNotaInternaPruebas.Sesion(CapacidadPrivilegio.Impersonacion)));

        (await handler.Handle(Ficha(t, new("Otro", "699999999")), CancellationToken.None)).EsExitoso.Should().BeTrue();

        t.ContactoEmergenciaNombre.Should().Be("Luis");
    }

    /// <summary>
    /// Paridad lectura/escritura, rol a rol de la lista real de <c>AutorizacionEscrituraBehavior</c> (como
    /// <c>ObtenerFichaTrabajadorTests</c> con el permiso de trabajo): todo rol que puede guardar la ficha recibe el
    /// contacto al leer y lo cambia al escribir. Un rol de escritura nuevo fuera de la política pone esto en rojo.
    /// </summary>
    [Fact]
    public async Task Todo_rol_que_puede_guardar_la_ficha_lee_y_escribe_el_contacto()
    {
        var rolesConEscritura = AutorizacionEscrituraBehavior<EditarFichaTrabajadorCommand, Result>.RolesConEscritura;
        rolesConEscritura.Should().NotBeEmpty("con la lista real vacía, el bucle de abajo no mediría nada");

        foreach (var rol in rolesConEscritura)
        {
            var (leido, consulta) = ConContacto(PoliticaNotaInternaPruebas.Con(rol));
            var dto = await consulta.Handle(new(leido.Id), CancellationToken.None);
            dto!.ContactoEmergenciaVisible.Should().BeTrue($"el rol {rol} puede guardar la ficha");
            dto.ContactoEmergenciaNombre.Should().Be("Luis Campos", $"el rol {rol} puede guardar la ficha");

            var t = Paula();
            var (handler, _) = Montar(t, politica: PoliticaNotaInternaPruebas.Con(rol));
            (await handler.Handle(Ficha(t, new("Luis", "611223344")), CancellationToken.None)).EsExitoso.Should().BeTrue();
            t.ContactoEmergenciaNombre.Should().Be("Luis", $"el rol {rol} puede guardar la ficha");
        }
    }

    // ── Rol: lo decide AutorizacionEscrituraBehavior, por ser ICommand ────

    private sealed class SinSesionPrivilegiada : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }

    private sealed class SinTenant : ITenantActual
    {
        public Guid? TenantId => null;
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData(null)]
    public async Task Ni_Consulta_ni_el_usuario_de_Cliente_llegan_a_guardar_el_contacto(string? rol)
    {
        var behavior = new AutorizacionEscrituraBehavior<EditarFichaTrabajadorCommand, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), rol), new SinSesionPrivilegiada(), new SinTenant());
        var llego = false;

        var resultado = await behavior.Handle(Ficha(Paula(), new("Luis", "611223344")),
            _ => { llego = true; return Task.FromResult(Result.Exito()); }, CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Autorizacion.SoloLectura");
        llego.Should().BeFalse();
    }

    // ── Lectura: ObtenerTrabajadorPorIdQuery ──────────────────────────────

    private static (Trabajador Trabajador, ObtenerTrabajadorPorIdQueryHandler Handler) ConContacto(IPoliticaNotaInterna politica)
    {
        var empresa = new Empresa("Montajes", "B12345674");
        var t = Trabajador.DeEmpresa(empresa.Id, "Paula", "Campos", "12345678Z", telefono: "+34600111222");
        t.FijarContactoEmergencia("Luis Campos", "611223344");
        var empresas = new EmpresasQueryContextFalso(); empresas.ListaEmpresas.Add(empresa);
        var trabajadores = new TrabajadoresQueryContextFalso(); trabajadores.ListaTrabajadores.Add(t);
        return (t, new ObtenerTrabajadorPorIdQueryHandler(empresas, trabajadores, new AlcanceDatosServiceFalso(), politica));
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    public async Task El_equipo_del_Tenant_recibe_el_contacto(string rol)
    {
        var (t, handler) = ConContacto(PoliticaNotaInternaPruebas.Con(rol));

        var dto = await handler.Handle(new(t.Id), CancellationToken.None);

        dto!.ContactoEmergenciaNombre.Should().Be("Luis Campos");
        dto.ContactoEmergenciaTelefono.Should().Be("611223344");
        dto.ContactoEmergenciaVisible.Should().BeTrue();
    }

    [Theory]
    [InlineData("Cliente")]
    [InlineData("RolQueNoExiste")]
    [InlineData(null)]
    public async Task El_usuario_de_Cliente_no_recibe_el_contacto(string? rol)
    {
        var (t, handler) = ConContacto(PoliticaNotaInternaPruebas.Con(rol));

        var dto = await handler.Handle(new(t.Id), CancellationToken.None);

        dto.Should().NotBeNull("el Trabajador sigue siendo visible; solo se retira el contacto");
        dto!.ContactoEmergenciaNombre.Should().BeNull();
        dto.ContactoEmergenciaTelefono.Should().BeNull();
        dto.ContactoEmergenciaVisible.Should().BeFalse();
        dto.Telefono.Should().Be("+34600111222", "el teléfono del propio Trabajador no cambia de regla");
    }

    [Fact]
    public async Task La_impersonacion_no_recibe_el_contacto()
    {
        var (t, handler) = ConContacto(
            PoliticaNotaInternaPruebas.Con("GestorCae", PoliticaNotaInternaPruebas.Sesion(CapacidadPrivilegio.Impersonacion)));

        var dto = await handler.Handle(new(t.Id), CancellationToken.None);

        dto!.ContactoEmergenciaNombre.Should().BeNull();
        dto.ContactoEmergenciaVisible.Should().BeFalse();
    }
}
