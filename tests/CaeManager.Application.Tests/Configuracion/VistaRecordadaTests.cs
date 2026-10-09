using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Configuracion.Commands.EliminarFiltroGuardado;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Application.Configuracion.Commands.GuardarVistaRecordada;
using CaeManager.Application.Configuracion.Commands.OlvidarVistaRecordada;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Configuracion;

/// <summary>
/// La vista recordada de un listado: una por Tenant, Usuario y pantalla, en la
/// fila reservada de <see cref="FiltroGuardado"/>. Aquí se prueba la regla de
/// Application —quién la lee y la escribe, que sustituye en vez de duplicar, y que
/// los casos de uso de los filtros con nombre no la alcanzan—. El Tenant lo
/// acotan el filtro global de EF y RLS, y eso lo prueba la integración.
/// </summary>
public class VistaRecordadaTests
{
    private const string Pantalla = PantallasConVistaRecordada.Clientes;
    private static readonly Guid Tenant = Guid.NewGuid();

    // ───────────────────────── Guardar ─────────────────────────

    [Fact]
    public async Task Guarda_la_vista_del_usuario_actual_en_la_fila_reservada()
    {
        var usuarioId = Guid.NewGuid();
        var repositorio = new FiltroGuardadoRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await Guardar(repositorio, usuarioId, "{\"estado\":\"Critico\"}", unitOfWork: unitOfWork);

        resultado.EsExitoso.Should().BeTrue();
        var fila = repositorio.Filtros.Should().ContainSingle().Subject;
        fila.UsuarioId.Should().Be(usuarioId);
        fila.Pantalla.Should().Be(Pantalla);
        fila.EsVistaRecordada.Should().BeTrue();
        fila.ValoresJson.Should().Be("{\"estado\":\"Critico\"}");
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Volver_a_guardar_sustituye_la_vista_y_no_crea_otra_fila()
    {
        var usuarioId = Guid.NewGuid();
        var repositorio = new FiltroGuardadoRepositorioFalso();
        await Guardar(repositorio, usuarioId, "{\"estado\":\"Critico\"}");
        var idDeLaPrimera = repositorio.Filtros.Single().Id;

        var resultado = await Guardar(repositorio, usuarioId, "{\"estado\":\"AlDia\"}");

        resultado.EsExitoso.Should().BeTrue();
        var fila = repositorio.Filtros.Should().ContainSingle().Subject;
        fila.Id.Should().Be(idDeLaPrimera, "es la misma fila, con los valores nuevos");
        fila.ValoresJson.Should().Be("{\"estado\":\"AlDia\"}");
    }

    [Fact]
    public async Task Otro_Usuario_guarda_la_suya_sin_pisar_la_del_primero()
    {
        var primero = Guid.NewGuid();
        var segundo = Guid.NewGuid();
        var repositorio = new FiltroGuardadoRepositorioFalso();
        await Guardar(repositorio, primero, "{\"de\":\"primero\"}");

        await Guardar(repositorio, segundo, "{\"de\":\"segundo\"}");

        repositorio.Filtros.Should().HaveCount(2);
        repositorio.Filtros.Single(f => f.UsuarioId == primero).ValoresJson.Should().Be("{\"de\":\"primero\"}");
        repositorio.Filtros.Single(f => f.UsuarioId == segundo).ValoresJson.Should().Be("{\"de\":\"segundo\"}");
    }

    [Fact]
    public async Task Cada_pantalla_recuerda_su_propia_vista()
    {
        var usuarioId = Guid.NewGuid();
        var repositorio = new FiltroGuardadoRepositorioFalso();
        await Guardar(repositorio, usuarioId, "{\"de\":\"clientes\"}", PantallasConVistaRecordada.Clientes);

        await Guardar(repositorio, usuarioId, "{\"de\":\"visitas\"}", PantallasConVistaRecordada.Visitas);

        repositorio.Filtros.Should().HaveCount(2);
        repositorio.Filtros.Single(f => f.Pantalla == PantallasConVistaRecordada.Clientes).ValoresJson.Should().Be("{\"de\":\"clientes\"}");
    }

    [Fact]
    public async Task Guardar_la_vista_no_toca_un_filtro_con_nombre_de_esa_pantalla()
    {
        var usuarioId = Guid.NewGuid();
        var repositorio = new FiltroGuardadoRepositorioFalso();
        repositorio.Agregar(new FiltroGuardado(usuarioId, Pantalla, "Críticos", "{\"de\":\"filtro\"}"));

        await Guardar(repositorio, usuarioId, "{\"de\":\"vista\"}");

        repositorio.Filtros.Should().HaveCount(2);
        repositorio.Filtros.Single(f => !f.EsVistaRecordada).ValoresJson.Should().Be("{\"de\":\"filtro\"}");
    }

    [Fact]
    public async Task Sin_usuario_identificado_no_se_guarda_y_el_fallo_es_legible()
    {
        var repositorio = new FiltroGuardadoRepositorioFalso();

        var resultado = await Guardar(repositorio, usuarioId: null, "{}");

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("VistaRecordada.SinUsuario");
        resultado.Error.Mensaje.Should().NotBeNullOrWhiteSpace();
        repositorio.Filtros.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sin_Tenant_activo_no_se_guarda_y_el_fallo_es_legible(bool tenantVacio)
    {
        var repositorio = new FiltroGuardadoRepositorioFalso();

        var resultado = await Guardar(repositorio, Guid.NewGuid(), "{}", tenant: new TenantActualFijo(tenantVacio ? Guid.Empty : null));

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("VistaRecordada.SinTenant");
        resultado.Error.Mensaje.Should().NotBeNullOrWhiteSpace();
        repositorio.Filtros.Should().BeEmpty();
    }

    /// <summary>
    /// Dos pestañas del mismo Usuario: esta no vio fila e insertó, pero la otra se
    /// adelantó y el índice único rechaza la inserción. No sube la excepción: se
    /// reintenta como sustitución y gana la última.
    /// </summary>
    [Fact]
    public async Task Si_otra_pestana_se_adelanta_al_insertar_reintenta_como_sustitucion_y_gana_la_ultima()
    {
        var usuarioId = Guid.NewGuid();
        var repositorio = new FiltroGuardadoRepositorioFalso();
        var descarte = new DescarteCambiosPendientesFalso();
        var unitOfWork = new UnitOfWorkConFallos(fallos: 1, () => new DbUpdateException("23505"), alFallar: () =>
        {
            // Lo que queda en la base tras el choque: la inserción de esta pestaña no entró,
            // la de la otra sí.
            repositorio.Filtros.Clear();
            repositorio.Agregar(FiltroGuardado.CrearVistaRecordada(usuarioId, Pantalla, "{\"de\":\"otra pestaña\"}"));
        });

        var resultado = await Guardar(repositorio, usuarioId, "{\"de\":\"esta pestaña\"}", unitOfWork: unitOfWork, descarte: descarte);

        resultado.EsExitoso.Should().BeTrue();
        repositorio.Filtros.Should().ContainSingle().Which.ValoresJson.Should().Be("{\"de\":\"esta pestaña\"}");
        unitOfWork.Intentos.Should().Be(2);
        descarte.VecesDescartado.Should().Be(1, "lo que dejó rastreado el guardado fallido no puede quedarse en el contexto del circuito");
    }

    [Fact]
    public async Task Si_el_guardado_falla_dos_veces_responde_con_un_fallo_legible_y_no_lanza()
    {
        var repositorio = new FiltroGuardadoRepositorioFalso();
        var descarte = new DescarteCambiosPendientesFalso();
        var unitOfWork = new UnitOfWorkConFallos(fallos: int.MaxValue, () => new DbUpdateException("la base no responde"));

        var resultado = await Guardar(repositorio, Guid.NewGuid(), "{}", unitOfWork: unitOfWork, descarte: descarte);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("VistaRecordada.NoGuardada");
        resultado.Error.Mensaje.Should().NotBeNullOrWhiteSpace();
        unitOfWork.Intentos.Should().Be(2, "un reintento, no un bucle");
        descarte.VecesDescartado.Should().Be(2);
    }

    [Fact]
    public void El_validador_admite_las_diez_pantallas_y_ninguna_otra()
    {
        var validador = new GuardarVistaRecordadaCommandValidator();

        PantallasConVistaRecordada.Admitidas.Should().BeEquivalentTo(
            "Trabajadores", "Empresas", "Clientes", "Documentos", "Centros", "Subcontratas", "Vehiculos", "Proyectos", "Visitas", "Gestiones");
        foreach (var pantalla in PantallasConVistaRecordada.Admitidas)
            validador.Validate(new GuardarVistaRecordadaCommand(pantalla, "{}")).IsValid.Should().BeTrue(pantalla);

        foreach (var ajena in new[] { "", "clientes", "Usuarios", "Clientes " })
            validador.Validate(new GuardarVistaRecordadaCommand(ajena, "{}")).IsValid.Should().BeFalse($"«{ajena}» no recuerda la vista");
    }

    [Fact]
    public void El_validador_rechaza_la_vista_vacia_y_la_que_pasa_del_tope()
    {
        var validador = new GuardarVistaRecordadaCommandValidator();
        var enElTope = new string('x', PantallasConVistaRecordada.LongitudMaximaValoresJson);

        validador.Validate(new GuardarVistaRecordadaCommand(Pantalla, "")).IsValid.Should().BeFalse();
        validador.Validate(new GuardarVistaRecordadaCommand(Pantalla, "   ")).IsValid.Should().BeFalse();
        validador.Validate(new GuardarVistaRecordadaCommand(Pantalla, enElTope)).IsValid.Should().BeTrue("el tope es inclusivo");
        validador.Validate(new GuardarVistaRecordadaCommand(Pantalla, enElTope + "x")).IsValid.Should().BeFalse();
    }

    /// <summary>
    /// Una pantalla tiene un solo nombre en la tabla: si los filtros guardados y la
    /// vista recordada la llamaran distinto, la clave dejaría de coincidir.
    /// </summary>
    [Fact]
    public void Toda_pantalla_con_filtros_guardados_recuerda_la_vista_con_la_misma_cadena()
    {
        PantallasConVistaRecordada.Admitidas.Should().Contain(PantallasConFiltrosGuardados.Admitidas);
        PantallasConVistaRecordada.Admitidas.Should().OnlyHaveUniqueItems();
        PantallasConVistaRecordada.Admitidas.Where(p => p.Length > 50).Should().BeEmpty("la columna Pantalla admite 50 caracteres");
    }

    // ───────────────────────── Obtener ─────────────────────────

    [Fact]
    public async Task Obtiene_la_vista_del_usuario_actual_y_no_la_de_otro()
    {
        var yo = Guid.NewGuid();
        var contexto = new ConfiguracionQueryContextFalso();
        contexto.ListaFiltrosGuardados.Add(FiltroGuardado.CrearVistaRecordada(Guid.NewGuid(), Pantalla, "{\"de\":\"otro\"}"));
        contexto.ListaFiltrosGuardados.Add(FiltroGuardado.CrearVistaRecordada(yo, Pantalla, "{\"de\":\"yo\"}"));

        (await Obtener(contexto, yo)).Should().Be("{\"de\":\"yo\"}");
    }

    [Fact]
    public async Task Si_solo_otro_Usuario_tiene_vista_el_actual_no_obtiene_ninguna()
    {
        var contexto = new ConfiguracionQueryContextFalso();
        contexto.ListaFiltrosGuardados.Add(FiltroGuardado.CrearVistaRecordada(Guid.NewGuid(), Pantalla, "{\"de\":\"otro\"}"));

        (await Obtener(contexto, Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task No_confunde_la_vista_con_un_filtro_con_nombre_ni_con_la_de_otra_pantalla()
    {
        var yo = Guid.NewGuid();
        var contexto = new ConfiguracionQueryContextFalso();
        contexto.ListaFiltrosGuardados.Add(new FiltroGuardado(yo, Pantalla, "Críticos", "{\"de\":\"filtro\"}"));
        contexto.ListaFiltrosGuardados.Add(FiltroGuardado.CrearVistaRecordada(yo, PantallasConVistaRecordada.Visitas, "{\"de\":\"visitas\"}"));

        (await Obtener(contexto, yo)).Should().BeNull("en Clientes solo hay un filtro con nombre");
        (await Obtener(contexto, yo, PantallasConVistaRecordada.Visitas)).Should().Be("{\"de\":\"visitas\"}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sin_usuario_o_sin_Tenant_no_hay_vista_que_devolver(bool sinUsuario)
    {
        var yo = Guid.NewGuid();
        var contexto = new ConfiguracionQueryContextFalso();
        contexto.ListaFiltrosGuardados.Add(FiltroGuardado.CrearVistaRecordada(yo, Pantalla, "{\"de\":\"yo\"}"));

        var handler = new ObtenerVistaRecordadaQueryHandler(
            contexto, new CurrentUserServiceFalso(sinUsuario ? null : yo), new TenantActualFijo(sinUsuario ? Tenant : null));

        (await handler.Handle(new ObtenerVistaRecordadaQuery(Pantalla), CancellationToken.None)).Should().BeNull();
    }

    // ───────────────────────── Olvidar ─────────────────────────

    [Fact]
    public async Task Olvidar_borra_solo_la_vista_del_usuario_en_esa_pantalla()
    {
        var yo = Guid.NewGuid();
        var otro = Guid.NewGuid();
        var repositorio = new FiltroGuardadoRepositorioFalso();
        repositorio.Agregar(FiltroGuardado.CrearVistaRecordada(yo, Pantalla, "{\"de\":\"yo\"}"));
        repositorio.Agregar(FiltroGuardado.CrearVistaRecordada(otro, Pantalla, "{\"de\":\"otro\"}"));
        repositorio.Agregar(FiltroGuardado.CrearVistaRecordada(yo, PantallasConVistaRecordada.Visitas, "{\"de\":\"visitas\"}"));
        repositorio.Agregar(new FiltroGuardado(yo, Pantalla, "Críticos", "{\"de\":\"filtro\"}"));

        var resultado = await Olvidar(repositorio, yo);

        resultado.EsExitoso.Should().BeTrue();
        repositorio.Filtros.Select(f => f.ValoresJson).Should().BeEquivalentTo(
            "{\"de\":\"otro\"}", "{\"de\":\"visitas\"}", "{\"de\":\"filtro\"}");
    }

    [Fact]
    public async Task Olvidar_es_idempotente()
    {
        var yo = Guid.NewGuid();
        var repositorio = new FiltroGuardadoRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        repositorio.Agregar(FiltroGuardado.CrearVistaRecordada(yo, Pantalla, "{}"));

        (await Olvidar(repositorio, yo, unitOfWork: unitOfWork)).EsExitoso.Should().BeTrue();
        (await Olvidar(repositorio, yo, unitOfWork: unitOfWork)).EsExitoso.Should().BeTrue("sin vista, lo pedido ya se cumple");

        repositorio.Filtros.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(1, "la segunda vez no hay nada que guardar");
    }

    [Fact]
    public async Task Si_otra_pestana_la_borro_a_la_vez_olvidar_sigue_siendo_un_exito()
    {
        var yo = Guid.NewGuid();
        var repositorio = new FiltroGuardadoRepositorioFalso();
        repositorio.Agregar(FiltroGuardado.CrearVistaRecordada(yo, Pantalla, "{}"));
        var descarte = new DescarteCambiosPendientesFalso();
        var unitOfWork = new UnitOfWorkConFallos(fallos: 1, () => new DbUpdateConcurrencyException("0 filas"));

        var resultado = await Olvidar(repositorio, yo, unitOfWork: unitOfWork, descarte: descarte);

        resultado.EsExitoso.Should().BeTrue();
        descarte.VecesDescartado.Should().Be(1);
    }

    [Fact]
    public async Task Olvidar_sin_usuario_o_sin_Tenant_falla_de_forma_legible()
    {
        var repositorio = new FiltroGuardadoRepositorioFalso();

        var sinUsuario = await Olvidar(repositorio, usuarioId: null);
        var sinTenant = await Olvidar(repositorio, Guid.NewGuid(), tenant: new TenantActualFijo(null));

        sinUsuario.Error.Codigo.Should().Be("VistaRecordada.SinUsuario");
        sinTenant.Error.Codigo.Should().Be("VistaRecordada.SinTenant");
    }

    [Fact]
    public void Olvidar_solo_admite_pantallas_que_recuerdan_la_vista()
    {
        var validador = new OlvidarVistaRecordadaCommandValidator();

        validador.Validate(new OlvidarVistaRecordadaCommand(Pantalla)).IsValid.Should().BeTrue();
        validador.Validate(new OlvidarVistaRecordadaCommand("Usuarios")).IsValid.Should().BeFalse();
    }

    // ───────────── Los filtros con nombre no alcanzan la fila reservada ─────────────

    [Theory]
    [InlineData("__vista_recordada__")]
    [InlineData("  __vista_recordada__ ")]
    [InlineData("__Vista_Recordada__")]
    public void El_nombre_reservado_no_se_puede_guardar_a_mano(string nombre)
    {
        var validador = new GuardarFiltroCommandValidator();

        var validacion = validador.Validate(new GuardarFiltroCommand(PantallasConFiltrosGuardados.Clientes, nombre, "{}"));

        validacion.IsValid.Should().BeFalse();
        validacion.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be("Ese nombre está reservado. Elige otro nombre.");
        validador.Validate(new GuardarFiltroCommand(PantallasConFiltrosGuardados.Clientes, "Vista recordada", "{}"))
            .IsValid.Should().BeTrue("un nombre corriente que se le parece sí se admite");
    }

    [Fact]
    public async Task El_listado_de_filtros_guardados_no_incluye_la_vista_recordada()
    {
        var yo = Guid.NewGuid();
        var contexto = new ConfiguracionQueryContextFalso();
        contexto.ListaFiltrosGuardados.Add(new FiltroGuardado(yo, Pantalla, "Críticos", "{\"de\":\"filtro\"}"));
        contexto.ListaFiltrosGuardados.Add(FiltroGuardado.CrearVistaRecordada(yo, Pantalla, "{\"de\":\"vista\"}"));

        var filtros = await new ObtenerFiltrosGuardadosQueryHandler(contexto, new CurrentUserServiceFalso(yo))
            .Handle(new ObtenerFiltrosGuardadosQuery(Pantalla), CancellationToken.None);

        filtros.Select(f => f.Nombre).Should().Equal("Críticos");
    }

    [Fact]
    public async Task Eliminar_por_Id_no_borra_la_vista_recordada_ni_a_su_dueno()
    {
        var yo = Guid.NewGuid();
        var vista = FiltroGuardado.CrearVistaRecordada(yo, Pantalla, "{}");
        var repositorio = new FiltroGuardadoRepositorioFalso();
        repositorio.Agregar(vista);
        var handler = new EliminarFiltroGuardadoCommandHandler(new CurrentUserServiceFalso(yo), repositorio, new UnitOfWorkFalso());

        var resultado = await handler.Handle(new EliminarFiltroGuardadoCommand(vista.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("FiltroGuardado.NoEncontrado", "responde igual que si el Id no existiera");
        repositorio.Filtros.Should().ContainSingle();
    }

    // ───────────────────────── Arnés ─────────────────────────

    private static Task<Result> Guardar(
        FiltroGuardadoRepositorioFalso repositorio, Guid? usuarioId, string valoresJson, string pantalla = Pantalla,
        TenantActualFijo? tenant = null, IUnitOfWork? unitOfWork = null, DescarteCambiosPendientesFalso? descarte = null) =>
        new GuardarVistaRecordadaCommandHandler(
                new CurrentUserServiceFalso(usuarioId), tenant ?? new TenantActualFijo(Tenant), repositorio,
                unitOfWork ?? new UnitOfWorkFalso(), descarte ?? new DescarteCambiosPendientesFalso(),
                NullLogger<GuardarVistaRecordadaCommandHandler>.Instance)
            .Handle(new GuardarVistaRecordadaCommand(pantalla, valoresJson), CancellationToken.None);

    private static Task<Result> Olvidar(
        FiltroGuardadoRepositorioFalso repositorio, Guid? usuarioId, TenantActualFijo? tenant = null,
        IUnitOfWork? unitOfWork = null, DescarteCambiosPendientesFalso? descarte = null) =>
        new OlvidarVistaRecordadaCommandHandler(
                new CurrentUserServiceFalso(usuarioId), tenant ?? new TenantActualFijo(Tenant), repositorio,
                unitOfWork ?? new UnitOfWorkFalso(), descarte ?? new DescarteCambiosPendientesFalso())
            .Handle(new OlvidarVistaRecordadaCommand(Pantalla), CancellationToken.None);

    private static Task<string?> Obtener(ConfiguracionQueryContextFalso contexto, Guid usuarioId, string pantalla = Pantalla) =>
        new ObtenerVistaRecordadaQueryHandler(contexto, new CurrentUserServiceFalso(usuarioId), new TenantActualFijo(Tenant))
            .Handle(new ObtenerVistaRecordadaQuery(pantalla), CancellationToken.None);

    /// <summary>Falla las primeras <paramref name="fallos"/> veces que se guarda y después guarda.</summary>
    private sealed class UnitOfWorkConFallos(int fallos, Func<Exception> excepcion, Action? alFallar = null) : IUnitOfWork
    {
        public int Intentos { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Intentos++;
            if (Intentos > fallos) return Task.FromResult(1);

            alFallar?.Invoke();
            throw excepcion();
        }
    }
}
