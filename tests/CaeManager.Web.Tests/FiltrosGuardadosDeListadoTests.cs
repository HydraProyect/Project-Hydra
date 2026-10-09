using Bunit;
using CaeManager.Application.Configuracion.Commands.EliminarFiltroGuardado;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La pieza compartida de filtros guardados (<see cref="FiltrosGuardadosDeListado"/>), sola: qué guarda
/// (los parámetros de la lista blanca que la URL lleva, y nada más), qué entrega al aplicar (la vista
/// entera, con <c>null</c> en lo que el filtro no trae), el error legible del nombre repetido y el
/// borrado con confirmación y sin relectura. Lo que cada listado hace con la vista que recibe se
/// prueba en <c>FiltrosGuardadosEnListadosTests</c>.
/// </summary>
public class FiltrosGuardadosDeListadoTests : BunitContext
{
    private static readonly IReadOnlyList<string> ListaBlanca = ["q", "estado"];

    private readonly MediatorDeFiltros _mediador = new();
    private readonly ConexionFiltrosGuardados _conexion = new();
    private readonly List<IReadOnlyDictionary<string, string?>> _aplicadas = [];
    private int _cambios;

    public FiltrosGuardadosDeListadoTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
    }

    private sealed class MediatorDeFiltros : IMediator
    {
        public List<object> Enviadas { get; } = [];
        public List<FiltroGuardadoDto> FiltrosGuardados { get; } = [];
        public Result<Guid>? ResultadoGuardar { get; set; }
        public bool FallaLaLectura { get; set; }
        public bool FallaElGuardado { get; set; }
        public bool FallaElBorrado { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            object respuesta = request switch
            {
                ObtenerFiltrosGuardadosQuery when FallaLaLectura => throw new InvalidOperationException("sin base"),
                ObtenerFiltrosGuardadosQuery => (IReadOnlyList<FiltroGuardadoDto>)FiltrosGuardados.ToList(),
                GuardarFiltroCommand when FallaElGuardado => throw new InvalidOperationException("23505: choque en el índice único"),
                GuardarFiltroCommand => ResultadoGuardar ?? Result.Exito(Guid.NewGuid()),
                EliminarFiltroGuardadoCommand when FallaElBorrado => throw new InvalidOperationException("sin base"),
                EliminarFiltroGuardadoCommand => Result.Exito(),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
            };
            return Task.FromResult((TResponse)respuesta);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<FiltrosGuardadosDeListado> Renderizar(string ruta = "empresas")
    {
        Navegacion.NavigateTo(ruta);
        return Render<FiltrosGuardadosDeListado>(p => p
            .Add(c => c.Conexion, _conexion)
            .Add(c => c.Pantalla, PantallasConFiltrosGuardados.Empresas)
            .Add(c => c.ParametrosDeVista, ListaBlanca)
            .Add(c => c.OnAplicar, vista => _aplicadas.Add(vista))
            .Add(c => c.AlCambiar, () => _cambios++));
    }

    private static FiltroGuardadoDto Filtro(string nombre, string valoresJson) =>
        new(Guid.NewGuid(), nombre, valoresJson, DateTime.UtcNow);

    private static Task EscribirNombreAsync(IRenderedComponent<FiltrosGuardadosDeListado> cut, string nombre) =>
        cut.Find("[role=dialog] input").InputAsync(new ChangeEventArgs { Value = nombre });

    private static AngleSharp.Dom.IElement BotonDelDialogo(IRenderedComponent<FiltrosGuardadosDeListado> cut, string texto) =>
        cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == texto);

    private async Task<IRenderedComponent<FiltrosGuardadosDeListado>> AbrirGuardarAsync(string ruta)
    {
        var cut = Renderizar(ruta);
        await cut.InvokeAsync(_conexion.AbrirGuardar);
        return cut;
    }

    // ---------------------------------------------------------------- carga

    [Fact]
    public void Carga_los_filtros_de_su_pantalla_y_los_ofrece_a_la_barra_avisando_a_la_pagina()
    {
        var filtro = Filtro("Vencidas", "{\"estado\":\"Vencido\"}");
        _mediador.FiltrosGuardados.Add(filtro);

        Renderizar();

        _mediador.Enviadas.OfType<ObtenerFiltrosGuardadosQuery>().Should().Equal([new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Empresas)]);
        _conexion.Opciones.Should().Equal([new OpcionEstado(filtro.Id.ToString(), "Vencidas")]);
        _cambios.Should().Be(1, "la página se repinta para que la barra vea las opciones");
    }

    [Fact]
    public void Si_la_lista_no_se_puede_leer_la_barra_sale_sin_filtros_guardados_y_la_pagina_sigue()
    {
        _mediador.FallaLaLectura = true;

        var montar = () => Renderizar();

        montar.Should().NotThrow("los filtros guardados son un accesorio de la lista");
        _mediador.Enviadas.OfType<ObtenerFiltrosGuardadosQuery>().Should().ContainSingle("la lectura se intentó");
        _conexion.Opciones.Should().BeEmpty();
    }

    // -------------------------------------------------------------- guardar

    [Fact]
    public async Task Guardar_envia_la_pantalla_y_solo_los_parametros_de_la_lista_blanca_que_lleva_la_url()
    {
        var cut = await AbrirGuardarAsync(
            $"empresas?q=Ebro&accion=crear&estado=Vencido&Nombre=Nueva&ClienteId={Guid.NewGuid()}&pagina=3");
        cut.Find("[role=dialog] h2").TextContent.Trim().Should().Be("Guardar filtro actual");
        await EscribirNombreAsync(cut, "Vencidas de Ebro");

        await BotonDelDialogo(cut, "Guardar").ClickAsync(new MouseEventArgs());

        var comando = _mediador.Enviadas.OfType<GuardarFiltroCommand>().Should().ContainSingle().Subject;
        comando.Pantalla.Should().Be(PantallasConFiltrosGuardados.Empresas);
        comando.Nombre.Should().Be("Vencidas de Ebro");
        comando.ValoresJson.Should().Be("{\"q\":\"Ebro\",\"estado\":\"Vencido\"}",
            "accion, las precargas del alta y la página no son la vista: no están en la lista blanca");
        cut.FindAll("[role=dialog]").Should().BeEmpty("guardado el filtro, el modal se cierra");
        _conexion.Opciones.Select(o => o.Texto).Should().Equal("Vencidas de Ebro");
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(m => m.Tono == TonoToast.Exito);
        _mediador.Enviadas.OfType<ObtenerFiltrosGuardadosQuery>().Should().ContainSingle("tras guardar no se relee la lista");
    }

    [Fact]
    public async Task Guardar_no_incluye_un_parametro_de_la_lista_que_la_url_lleva_vacio_y_no_distingue_mayusculas_en_el_nombre()
    {
        var cut = await AbrirGuardarAsync("empresas?Q=Ebro&estado=");
        await EscribirNombreAsync(cut, "Ebro");

        await BotonDelDialogo(cut, "Guardar").ClickAsync(new MouseEventArgs());

        _mediador.Enviadas.OfType<GuardarFiltroCommand>().Single().ValoresJson.Should().Be("{\"q\":\"Ebro\"}",
            "el parámetro se guarda con la grafía de la lista blanca, y el vacío no está");
    }

    [Fact]
    public async Task Sin_nombre_guardar_esta_deshabilitado_y_dice_por_que()
    {
        var cut = await AbrirGuardarAsync("empresas?q=Ebro");

        BotonDelDialogo(cut, "Guardar").HasAttribute("disabled").Should().BeTrue();
        BotonDelDialogo(cut, "Guardar").GetAttribute("title").Should().Be("Escribe un nombre para el filtro");
        _mediador.Enviadas.OfType<GuardarFiltroCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Un_nombre_repetido_se_dice_dentro_del_modal_y_no_pierde_lo_escrito()
    {
        _mediador.ResultadoGuardar = Result.Fallo<Guid>(Error.Crear(
            "FiltroGuardado.NombreDuplicado", "Ya tienes un filtro guardado con ese nombre en esta pantalla. Elige otro nombre."));
        var cut = await AbrirGuardarAsync("empresas?q=Ebro");
        await EscribirNombreAsync(cut, "Vencidas");

        await BotonDelDialogo(cut, "Guardar").ClickAsync(new MouseEventArgs());

        var dialogo = cut.Find("[role=dialog]");
        dialogo.TextContent.Should().Contain("Ya tienes un filtro guardado con ese nombre en esta pantalla");
        cut.FindComponent<CampoTexto>().Instance.Valor.Should().Be("Vencidas");
        _conexion.Opciones.Should().BeEmpty("no se guardó nada");
        Services.GetRequiredService<ToastService>().Mensajes.Should().BeEmpty();
    }

    /// <summary>
    /// El guardado LANZA (dos pestañas guardando el mismo nombre chocan en el índice único después de que el
    /// validador lo diera por libre; la base no responde). Sin capturarlo, la excepción sube al límite de
    /// errores de la página y la sustituye entera: aquí se dice dentro del modal, que no se cierra.
    /// </summary>
    [Fact]
    public async Task Si_el_guardado_lanza_se_dice_dentro_del_modal_que_sigue_abierto_con_el_nombre_escrito()
    {
        _mediador.FallaElGuardado = true;
        var cut = await AbrirGuardarAsync("empresas?q=Ebro");
        await EscribirNombreAsync(cut, "Vencidas");

        var guardar = () => BotonDelDialogo(cut, "Guardar").ClickAsync(new MouseEventArgs());

        await guardar.Should().NotThrowAsync("una excepción aquí sustituye la página entera por el límite de errores");
        _mediador.Enviadas.OfType<GuardarFiltroCommand>().Should().ContainSingle("control positivo: el guardado se intentó");
        cut.Find("[role=dialog]").TextContent.Should().Contain("No pudimos guardar el filtro");
        cut.FindComponent<CampoTexto>().Instance.Valor.Should().Be("Vencidas");
        BotonDelDialogo(cut, "Guardar").HasAttribute("disabled").Should().BeFalse("se puede reintentar");
        _conexion.Opciones.Should().BeEmpty("no se guardó nada");
        Services.GetRequiredService<ToastService>().Mensajes.Should().BeEmpty();

        // Reintento con la base de vuelta: el mismo modal guarda y se cierra.
        _mediador.FallaElGuardado = false;
        await BotonDelDialogo(cut, "Guardar").ClickAsync(new MouseEventArgs());

        cut.FindAll("[role=dialog]").Should().BeEmpty();
        _conexion.Opciones.Select(o => o.Texto).Should().Equal("Vencidas");
    }

    /// <summary>
    /// La lectura inicial falló: la barra salió sin filtros guardados, pero el usuario SÍ tenía. Si al guardar
    /// uno nuevo se añadiera a esa lista vacía, la barra enseñaría solo el nuevo y los anteriores parecerían
    /// borrados. Tras un guardado correcto se relee la lista entera.
    /// </summary>
    [Fact]
    public async Task Si_la_lectura_inicial_fallo_tras_guardar_se_relee_la_lista_entera()
    {
        _mediador.FiltrosGuardados.Add(Filtro("Antiguo", "{\"estado\":\"Vencido\"}"));
        _mediador.FallaLaLectura = true;
        var cut = await AbrirGuardarAsync("empresas?q=Ebro");
        _conexion.Opciones.Should().BeEmpty("control positivo: la lectura inicial falló y la barra salió sin filtros");
        await EscribirNombreAsync(cut, "Ebro");

        // La base vuelve; el guardado deja el nuevo junto al que ya había.
        _mediador.FallaLaLectura = false;
        _mediador.FiltrosGuardados.Add(Filtro("Ebro", "{\"q\":\"Ebro\"}"));
        await BotonDelDialogo(cut, "Guardar").ClickAsync(new MouseEventArgs());

        cut.FindAll("[role=dialog]").Should().BeEmpty();
        _mediador.Enviadas.OfType<ObtenerFiltrosGuardadosQuery>().Should().HaveCount(2, "la inicial fallida y la relectura");
        _conexion.Opciones.Select(o => o.Texto).Should().Equal(["Antiguo", "Ebro"], "los que ya tenía no parecen borrados");

        // Ya con la lista buena, el siguiente guardado vuelve a añadir sin releer.
        await cut.InvokeAsync(_conexion.AbrirGuardar);
        await EscribirNombreAsync(cut, "Otro");
        await BotonDelDialogo(cut, "Guardar").ClickAsync(new MouseEventArgs());

        _mediador.Enviadas.OfType<ObtenerFiltrosGuardadosQuery>().Should().HaveCount(2, "la lista ya es la del usuario: no se relee más");
        _conexion.Opciones.Select(o => o.Texto).Should().Equal(["Antiguo", "Ebro", "Otro"]);
    }

    [Fact]
    public async Task Si_la_relectura_tras_guardar_tambien_falla_se_anade_el_nuevo_y_el_modal_se_cierra()
    {
        _mediador.FallaLaLectura = true;
        var cut = await AbrirGuardarAsync("empresas?q=Ebro");
        await EscribirNombreAsync(cut, "Ebro");

        await BotonDelDialogo(cut, "Guardar").ClickAsync(new MouseEventArgs());

        cut.FindAll("[role=dialog]").Should().BeEmpty("el filtro ya está guardado: el modal no se queda abierto sobre él");
        _mediador.Enviadas.OfType<ObtenerFiltrosGuardadosQuery>().Should().HaveCount(2, "la inicial y el intento de relectura");
        _conexion.Opciones.Select(o => o.Texto).Should().Equal("Ebro");

        // Sigue sin lista buena: el siguiente guardado lo vuelve a intentar.
        _mediador.FallaLaLectura = false;
        _mediador.FiltrosGuardados.AddRange([Filtro("Antiguo", "{}"), Filtro("Ebro", "{}"), Filtro("Otro", "{}")]);
        await cut.InvokeAsync(_conexion.AbrirGuardar);
        await EscribirNombreAsync(cut, "Otro");
        await BotonDelDialogo(cut, "Guardar").ClickAsync(new MouseEventArgs());

        _conexion.Opciones.Select(o => o.Texto).Should().Equal(["Antiguo", "Ebro", "Otro"]);
    }

    // -------------------------------------------------------------- aplicar

    [Fact]
    public async Task Aplicar_entrega_la_vista_entera_null_en_lo_que_el_filtro_no_trae_y_nada_de_fuera_de_la_lista()
    {
        var filtro = Filtro("Solo Ebro", "{\"q\":\"Ebro\",\"accion\":\"crear\",\"ClienteId\":\"x\"}");
        _mediador.FiltrosGuardados.Add(filtro);
        var cut = Renderizar("empresas?estado=Vencido");

        await cut.InvokeAsync(() => _conexion.AplicarAsync(filtro.Id.ToString()));

        var vista = _aplicadas.Should().ContainSingle().Subject;
        vista.Should().BeEquivalentTo(new Dictionary<string, string?> { ["q"] = "Ebro", ["estado"] = null },
            "la página recibe un valor por cada parámetro de la lista blanca —null el ausente, que quita— y ninguno más");
    }

    [Theory]
    [InlineData("no es json")]
    [InlineData("[\"q\"]")]
    [InlineData("\"q\"")]
    public async Task Un_valor_guardado_que_no_es_un_diccionario_no_se_aplica(string valoresJson)
    {
        var filtro = Filtro("Roto", valoresJson);
        _mediador.FiltrosGuardados.Add(filtro);
        var cut = Renderizar("empresas?estado=Vencido");

        await cut.InvokeAsync(() => _conexion.AplicarAsync(filtro.Id.ToString()));

        _aplicadas.Should().BeEmpty("lo que no es una vista no borra la que hay");
    }

    [Fact]
    public async Task Un_valor_que_no_es_texto_cuenta_como_ausente()
    {
        var filtro = Filtro("Raro", "{\"q\":7,\"estado\":\"Vencido\"}");
        _mediador.FiltrosGuardados.Add(filtro);
        var cut = Renderizar();

        await cut.InvokeAsync(() => _conexion.AplicarAsync(filtro.Id.ToString()));

        _aplicadas.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new Dictionary<string, string?> { ["q"] = null, ["estado"] = "Vencido" });
    }

    [Fact]
    public async Task Aplicar_un_id_que_no_esta_en_la_lista_no_hace_nada()
    {
        _mediador.FiltrosGuardados.Add(Filtro("Solo Ebro", "{\"q\":\"Ebro\"}"));
        var cut = Renderizar();

        await cut.InvokeAsync(() => _conexion.AplicarAsync(Guid.NewGuid().ToString()));

        _aplicadas.Should().BeEmpty();
    }

    // --------------------------------------------------------------- borrar

    [Fact]
    public async Task Borrar_pide_confirmacion_y_al_confirmar_lo_quita_sin_releer_la_lista()
    {
        var filtro = Filtro("Solo Ebro", "{\"q\":\"Ebro\"}");
        _mediador.FiltrosGuardados.Add(filtro);
        _mediador.FiltrosGuardados.Add(Filtro("Vencidas", "{\"estado\":\"Vencido\"}"));
        var cut = Renderizar();

        await cut.InvokeAsync(() => _conexion.PedirBorrar(filtro.Id.ToString()));

        _mediador.Enviadas.OfType<EliminarFiltroGuardadoCommand>().Should().BeEmpty("pulsar el aspa solo pide confirmación");
        cut.Find("[role=dialog] h2").TextContent.Trim().Should().Be("¿Borrar el filtro guardado «Solo Ebro»?");

        await BotonDelDialogo(cut, "Borrar filtro").ClickAsync(new MouseEventArgs());

        _mediador.Enviadas.OfType<EliminarFiltroGuardadoCommand>().Should().Equal([new EliminarFiltroGuardadoCommand(filtro.Id)]);
        cut.FindAll("[role=dialog]").Should().BeEmpty();
        _conexion.Opciones.Select(o => o.Texto).Should().Equal("Vencidas");
        _mediador.Enviadas.OfType<ObtenerFiltrosGuardadosQuery>().Should().ContainSingle(
            "borrado el filtro no se relee la lista: si esa relectura fallara, la confirmación quedaría abierta sobre un filtro que ya no existe");
    }

    [Fact]
    public async Task Cancelar_el_borrado_no_borra_nada()
    {
        var filtro = Filtro("Solo Ebro", "{\"q\":\"Ebro\"}");
        _mediador.FiltrosGuardados.Add(filtro);
        var cut = Renderizar();
        await cut.InvokeAsync(() => _conexion.PedirBorrar(filtro.Id.ToString()));

        await BotonDelDialogo(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        _mediador.Enviadas.OfType<EliminarFiltroGuardadoCommand>().Should().BeEmpty();
        cut.FindAll("[role=dialog]").Should().BeEmpty();
        _conexion.Opciones.Select(o => o.Texto).Should().Equal("Solo Ebro");
    }

    [Fact]
    public async Task Si_el_borrado_falla_avisa_con_un_toast_y_el_filtro_sigue_en_la_lista()
    {
        var filtro = Filtro("Solo Ebro", "{\"q\":\"Ebro\"}");
        _mediador.FiltrosGuardados.Add(filtro);
        _mediador.FallaElBorrado = true;
        var cut = Renderizar();
        await cut.InvokeAsync(() => _conexion.PedirBorrar(filtro.Id.ToString()));

        await BotonDelDialogo(cut, "Borrar filtro").ClickAsync(new MouseEventArgs());

        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(m => m.Tono == TonoToast.Error)
            .Which.Mensaje.Should().Be("No pudimos borrar el filtro guardado. Intenta nuevamente en unos segundos.");
        _conexion.Opciones.Select(o => o.Texto).Should().Equal("Solo Ebro");
    }

    // ------------------------------------------------------------- conexión

    [Fact]
    public async Task Sin_pieza_montada_la_conexion_no_ofrece_nada_y_sus_gestos_no_hacen_nada()
    {
        var conexion = new ConexionFiltrosGuardados();

        conexion.Opciones.Should().BeEmpty();
        var gestos = async () =>
        {
            conexion.AbrirGuardar();
            conexion.PedirBorrar(Guid.NewGuid().ToString());
            await conexion.AplicarAsync(Guid.NewGuid().ToString());
        };
        await gestos.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Retirada_la_pieza_la_conexion_deja_de_atender()
    {
        var filtro = Filtro("Solo Ebro", "{\"q\":\"Ebro\"}");
        _mediador.FiltrosGuardados.Add(filtro);
        var cut = Renderizar();

        await DisposeComponentsAsync();
        await _conexion.AplicarAsync(filtro.Id.ToString());

        _aplicadas.Should().BeEmpty();
        _ = cut;
    }
}
