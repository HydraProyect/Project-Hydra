using System.Security.Cryptography;
using System.Text;
using CaeManager.Domain.AltaAutonoma;
using FluentAssertions;

namespace CaeManager.Domain.Tests.AltaAutonoma;

public class SolicitudAltaTests
{
    /// <summary>
    /// Revisión de A4: la base compara el CIF enviado con Empresas."Cif" reducido a ASCII alfanumérico en mayúsculas. Eso
    /// solo es un superconjunto seguro si todo CIF o NIF que la aplicación acepta y normaliza es ASCII [0-9A-Z], también
    /// cuando llega con espacios Unicode (U+00A0, U+2007, U+3000), tabuladores, puntos o guiones.
    /// </summary>
    [Theory]
    [InlineData("B12345674", "B12345674")]
    [InlineData("b 12345674", "B12345674")]
    [InlineData("B 12.345.674", "B12345674")]
    [InlineData("　b-12345674\t", "B12345674")]
    [InlineData("1234567 l", "01234567L")]
    [InlineData("x1234567l", "X1234567L")]
    public void El_CIF_normalizado_que_se_compara_en_la_base_es_ASCII_alfanumerico_en_mayusculas(string escrito, string esperado)
    {
        var normalizado = SolicitudAlta.NormalizarCifNif(escrito);

        normalizado.Should().Be(esperado).And.MatchRegex("^[0-9A-Z]+$");
        normalizado.Should().OnlyContain(c => c < 128);
    }

    [Theory]
    [InlineData("B１２３４５６７４")]
    [InlineData("Ｂ12345674")]
    [InlineData("B12345674​")]
    public void Un_CIF_con_caracteres_que_no_son_ASCII_no_se_acepta(string escrito)
    {
        var accion = () => SolicitudAlta.NormalizarCifNif(escrito);

        accion.Should().Throw<ArgumentException>();
    }

    private static readonly DateTime Inicio = new(2026, 11, 4, 10, 0, 0, DateTimeKind.Utc);
    private static readonly string Token = new('t', 43);
    private const string CifEmpresa = "B12345674";
    private const string Dni = "12345678Z";
    private const string Sesion = "cs_test_1";

    private static SolicitudAlta Iniciada() =>
        SolicitudAlta.Iniciar(" Ana@Ejemplo.es ", "AQAAAAIAAYagAAAAE-hash", Token, "203.0.113.7", "Navegador/1.0", Inicio);

    private static AceptacionDeclarada Aceptacion(string documento = "Condiciones generales") =>
        new(documento, "2026-11-01", new string('a', 64), DeclaraFacultades: true, "203.0.113.7", Inicio.AddHours(1));

    /// <summary>Lleva una Solicitud de alta nueva al estado pedido por su camino permitido.</summary>
    private static SolicitudAlta En(EstadoSolicitudAlta estado)
    {
        var s = Iniciada();
        var t = Inicio.AddHours(1);
        if (estado == EstadoSolicitudAlta.Iniciada)
            return s;

        s.VerificarCorreo(Token, t).Should().BeTrue();
        if (estado == EstadoSolicitudAlta.CorreoVerificado)
            return s;

        if (estado == EstadoSolicitudAlta.DerivadaAAcceso)
        {
            s.DerivarAAcceso(CifEmpresa, t);
            return s;
        }

        if (estado == EstadoSolicitudAlta.Caducada)
        {
            s.Caducar(s.CaducaEnUtc);
            return s;
        }

        if (estado == EstadoSolicitudAlta.Bloqueada)
        {
            s.Bloquear("Regla antiabuso", t);
            return s;
        }

        s.CompletarDatos("Construcciones Ejemplo SL", CifEmpresa, "Calle Mayor 1, Tortosa", TramoTrabajadoresActivos.De11A40, true, false, t, "+34 977 000 000");
        if (estado == EstadoSolicitudAlta.DatosCompletos)
            return s;

        s.PasarAPendientePago(ModalidadSuscripcionAlta.Anual, [Aceptacion()], Sesion, t);
        if (estado == EstadoSolicitudAlta.PendientePago)
            return s;

        s.MarcarPagada(Sesion, "cus_1", "sub_1", t);
        if (estado == EstadoSolicitudAlta.Pagada)
            return s;

        if (estado == EstadoSolicitudAlta.Completada)
            s.Completar(Guid.NewGuid(), t);
        else if (estado == EstadoSolicitudAlta.Reembolsada)
            s.Reembolsar("CIF ocupado entre la reserva y el pago", t);
        else
            throw new ArgumentOutOfRangeException(nameof(estado));

        return s;
    }

    public static TheoryData<EstadoSolicitudAlta> TodosLosEstados()
    {
        var datos = new TheoryData<EstadoSolicitudAlta>();
        foreach (var e in Enum.GetValues<EstadoSolicitudAlta>())
            datos.Add(e);
        return datos;
    }

    // ── Iniciar ──

    [Fact]
    public void Iniciar_guarda_el_correo_normalizado_el_hash_del_token_y_los_plazos()
    {
        var s = Iniciada();

        s.Estado.Should().Be(EstadoSolicitudAlta.Iniciada);
        s.Correo.Should().Be("Ana@Ejemplo.es");
        s.CorreoNormalizado.Should().Be("ANA@EJEMPLO.ES");
        s.HashContrasena.Should().Be("AQAAAAIAAYagAAAAE-hash");
        s.IpOrigen.Should().Be("203.0.113.7");
        s.UserAgentOrigen.Should().Be("Navegador/1.0");
        s.CreadaEnUtc.Should().Be(Inicio);
        s.CaducaEnUtc.Should().Be(Inicio.AddDays(7));
        s.TokenVerificacionCaducaEnUtc.Should().Be(Inicio.AddHours(24));
        s.TenantIdCreado.Should().BeNull();
    }

    [Fact]
    public void El_token_nunca_se_guarda_en_claro_solo_su_SHA256()
    {
        var s = Iniciada();
        var esperado = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Token)));

        s.HashTokenVerificacion.Should().Be(esperado).And.NotContain(Token);
        SolicitudAlta.CalcularHashToken(Token).Should().Be(esperado);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sin-arroba")]
    [InlineData("dos@@ejemplo.es")]
    [InlineData("@ejemplo.es")]
    [InlineData("ana@")]
    [InlineData("ana@ejemplo")]
    [InlineData("ana @ejemplo.es")]
    public void Iniciar_rechaza_un_correo_mal_formado(string correo)
    {
        var accion = () => SolicitudAlta.Iniciar(correo, "hash", Token, "203.0.113.7", null, Inicio);
        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Iniciar_mide_la_longitud_del_correo_ya_normalizado()
    {
        // U+0958 es una exclusión de composición: la forma Unicode C la descompone en dos caracteres.
        var correo = new string('क़', 200) + "@ejemplo.es";
        correo.Length.Should().BeLessThanOrEqualTo(SolicitudAlta.LongitudMaximaCorreo, "guarda del instrumento: la entrada cabe");
        SolicitudAlta.NormalizarCorreo(correo).Length.Should().BeGreaterThan(SolicitudAlta.LongitudMaximaCorreo,
            "guarda del instrumento: la forma normalizada no cabe");

        var accion = () => SolicitudAlta.Iniciar(correo, "hash", Token, "203.0.113.7", null, Inicio);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Iniciar_admite_un_correo_que_crece_al_normalizarse_si_aun_cabe()
    {
        var correo = new string('क़', 100) + "@ejemplo.es";

        var s = SolicitudAlta.Iniciar(correo, "hash", Token, "203.0.113.7", null, Inicio);

        s.CorreoNormalizado!.Length.Should().BeGreaterThan(correo.Length).And.BeLessThanOrEqualTo(SolicitudAlta.LongitudMaximaCorreo);
    }

    [Fact]
    public void Las_aceptaciones_no_se_pueden_vaciar_desde_fuera()
    {
        var s = En(EstadoSolicitudAlta.PendientePago);
        s.Aceptaciones.Should().NotBeEmpty("guarda del instrumento");

        s.Aceptaciones.Should().NotBeAssignableTo<List<AceptacionCondicionesAlta>>();
        var comoColeccion = s.Aceptaciones as ICollection<AceptacionCondicionesAlta>;
        if (comoColeccion is not null)
        {
            comoColeccion.IsReadOnly.Should().BeTrue();
            comoColeccion.Invoking(c => c.Clear()).Should().Throw<NotSupportedException>();
        }

        s.Aceptaciones.Should().NotBeEmpty();
    }

    [Fact]
    public void Iniciar_rechaza_un_token_corto_una_IP_invalida_un_hash_vacio_y_una_hora_que_no_es_UTC()
    {
        ((Action)(() => SolicitudAlta.Iniciar("a@b.es", "hash", "corto", "203.0.113.7", null, Inicio))).Should().Throw<ArgumentException>();
        ((Action)(() => SolicitudAlta.Iniciar("a@b.es", "hash", Token, "no-es-ip", null, Inicio))).Should().Throw<ArgumentException>();
        ((Action)(() => SolicitudAlta.Iniciar("a@b.es", " ", Token, "203.0.113.7", null, Inicio))).Should().Throw<ArgumentException>();
        ((Action)(() => SolicitudAlta.Iniciar("a@b.es", "hash", Token, "203.0.113.7", null, DateTime.SpecifyKind(Inicio, DateTimeKind.Local))))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void El_user_agent_largo_se_recorta_y_uno_vacio_queda_null()
    {
        SolicitudAlta.Iniciar("a@b.es", "hash", Token, "2001:db8::1", new string('x', 2000), Inicio)
            .UserAgentOrigen.Should().HaveLength(SolicitudAlta.LongitudMaximaUserAgent);
        SolicitudAlta.Iniciar("a@b.es", "hash", Token, "2001:db8::1", "  ", Inicio).UserAgentOrigen.Should().BeNull();
    }

    // ── Verificación del correo ──

    [Fact]
    public void Verificar_con_el_token_correcto_pasa_a_CorreoVerificado_y_consume_el_token()
    {
        var s = Iniciada();
        var version = s.VersionFila;

        s.VerificarCorreo(Token, Inicio.AddHours(23)).Should().BeTrue();

        s.Estado.Should().Be(EstadoSolicitudAlta.CorreoVerificado);
        s.CorreoVerificadoEnUtc.Should().Be(Inicio.AddHours(23));
        s.HashTokenVerificacion.Should().BeNull();
        s.TokenVerificacionCaducaEnUtc.Should().BeNull();
        s.VersionFila.Should().Be(version + 1);
    }

    [Fact]
    public void Un_token_equivocado_o_caducado_no_verifica_ni_cambia_nada()
    {
        var s = Iniciada();

        s.VerificarCorreo(new string('x', 43), Inicio.AddHours(1)).Should().BeFalse();
        s.VerificarCorreo("corto", Inicio.AddHours(1)).Should().BeFalse();
        s.VerificarCorreo(Token, Inicio.AddHours(24)).Should().BeFalse();

        s.Estado.Should().Be(EstadoSolicitudAlta.Iniciada);
        s.HashTokenVerificacion.Should().NotBeNull();
        s.VersionFila.Should().Be(0);
    }

    [Fact]
    public void Renovar_el_token_invalida_el_anterior_y_reinicia_las_24_horas()
    {
        var s = Iniciada();
        var nuevo = new string('n', 43);

        s.RenovarTokenVerificacion(nuevo, Inicio.AddHours(30));

        s.TokenVerificacionCaducaEnUtc.Should().Be(Inicio.AddHours(54));
        s.VerificarCorreo(Token, Inicio.AddHours(31)).Should().BeFalse();
        s.VerificarCorreo(nuevo, Inicio.AddHours(31)).Should().BeTrue();
    }

    [Fact]
    public void Un_token_ya_usado_no_vuelve_a_verificar()
    {
        var s = En(EstadoSolicitudAlta.CorreoVerificado);

        var accion = () => s.VerificarCorreo(Token, Inicio.AddHours(2));

        accion.Should().Throw<TransicionSolicitudAltaNoPermitidaException>();
    }

    // ── Credencial de continuación (A4) ──

    [Fact]
    public void Verificar_guarda_solo_el_hash_del_credencial_de_continuacion_y_le_da_dos_horas()
    {
        var s = Iniciada();
        var credencial = new string('c', 43);

        s.VerificarCorreo(Token, Inicio.AddHours(1), credencial).Should().BeTrue();

        s.HashCredencialContinuacion.Should().Be(SolicitudAlta.CalcularHashToken(credencial)).And.NotBe(credencial);
        s.CredencialContinuacionCaducaEnUtc.Should().Be(Inicio.AddHours(3));
    }

    [Fact]
    public void El_credencial_de_continuacion_no_dura_mas_que_la_Solicitud_de_alta()
    {
        var s = Iniciada();
        var nuevo = new string('n', 43);
        s.RenovarTokenVerificacion(nuevo, s.CaducaEnUtc.AddHours(-3));

        s.VerificarCorreo(nuevo, s.CaducaEnUtc.AddHours(-1), new string('c', 43)).Should().BeTrue();

        s.CredencialContinuacionCaducaEnUtc.Should().Be(s.CaducaEnUtc);
    }

    [Fact]
    public void Un_token_que_no_casa_no_guarda_credencial()
    {
        var s = Iniciada();

        s.VerificarCorreo(new string('x', 43), Inicio.AddHours(1), new string('c', 43)).Should().BeFalse();

        s.HashCredencialContinuacion.Should().BeNull();
        s.CredencialContinuacionCaducaEnUtc.Should().BeNull();
    }

    [Fact]
    public void El_credencial_sigue_en_DatosCompletos_y_se_borra_al_derivar_a_acceso()
    {
        var completa = Iniciada();
        completa.VerificarCorreo(Token, Inicio.AddHours(1), new string('c', 43));
        completa.CompletarDatos("Construcciones Ejemplo SL", CifEmpresa, "Calle Mayor 1, Tortosa", TramoTrabajadoresActivos.De11A40, true, false, Inicio.AddHours(1));
        completa.HashCredencialContinuacion.Should().NotBeNull("el paso siguiente lo necesita");

        var derivada = Iniciada();
        derivada.VerificarCorreo(Token, Inicio.AddHours(1), new string('c', 43));
        derivada.DerivarAAcceso(CifEmpresa, Inicio.AddHours(1));
        derivada.HashCredencialContinuacion.Should().BeNull("un estado final no admite más pasos");
        derivada.CredencialContinuacionCaducaEnUtc.Should().BeNull();
    }

    [Fact]
    public void Caducar_y_borrar_los_datos_personales_quita_el_credencial()
    {
        var s = Iniciada();
        s.VerificarCorreo(Token, Inicio.AddHours(1), new string('c', 43));

        s.Caducar(s.CaducaEnUtc);
        s.BorrarDatosPersonales(s.CaducaEnUtc.AddDays(1));

        s.HashCredencialContinuacion.Should().BeNull();
        s.CredencialContinuacionCaducaEnUtc.Should().BeNull();
    }

    [Theory]
    [InlineData("", "B12345674", "Calle 1", true, false, null, "razonSocial")]
    [InlineData("Empresa SL", "B12345670", "Calle 1", true, false, null, "cifNif")]
    [InlineData("Empresa SL", "B12345674", " ", true, false, null, "domicilioFiscal")]
    [InlineData("Empresa SL", "B12345674", "Calle 1", true, false, "abc", "telefono")]
    [InlineData("Empresa SL", "B12345674", "Calle 1", false, false, null, "declaraRepresentacion")]
    [InlineData("Empresa SL", "12345678Z", "Calle 1", true, false, null, "declaraActividadProfesional")]
    public void DatosOrganizacionAlta_rechaza_con_el_nombre_del_campo_que_falla(
        string razonSocial, string cif, string domicilio, bool representacion, bool actividad, string? telefono, string campo)
    {
        var accion = () => DatosOrganizacionAlta.Crear(razonSocial, cif, domicilio, TramoTrabajadoresActivos.De1A10, representacion, actividad, telefono);

        accion.Should().Throw<ArgumentException>().Which.ParamName.Should().Be(campo);
    }

    [Fact]
    public void DatosOrganizacionAlta_rechaza_un_tramo_fuera_del_enum()
    {
        var accion = () => DatosOrganizacionAlta.Crear("Empresa SL", CifEmpresa, "Calle 1", (TramoTrabajadoresActivos)99, true, false);

        accion.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("tramo");
    }

    [Fact]
    public void DatosOrganizacionAlta_normaliza_como_el_agregado()
    {
        var datos = DatosOrganizacionAlta.Crear(" Empresa SL ", "b-1234567-4", " Calle 1 ", TramoTrabajadoresActivos.De81A150, true, false, "  ");

        datos.RazonSocial.Should().Be("Empresa SL");
        datos.CifNif.Should().Be(CifEmpresa);
        datos.DomicilioFiscal.Should().Be("Calle 1");
        datos.Telefono.Should().BeNull();
        datos.EsPersonaFisica.Should().BeFalse();
        DatosOrganizacionAlta.Crear("Ana", Dni, "Calle 1", TramoTrabajadoresActivos.De1A10, true, true).EsPersonaFisica.Should().BeTrue();
    }

    // ── Datos de la organización ──

    [Fact]
    public void Completar_datos_normaliza_el_CIF_y_pasa_a_DatosCompletos()
    {
        var s = En(EstadoSolicitudAlta.CorreoVerificado);

        s.CompletarDatos(" Construcciones Ejemplo SL ", "b-1234567.4", " Calle Mayor 1 ", TramoTrabajadoresActivos.De81A150, true, false, Inicio.AddHours(2));

        s.Estado.Should().Be(EstadoSolicitudAlta.DatosCompletos);
        s.CifNif.Should().Be(CifEmpresa);
        s.RazonSocial.Should().Be("Construcciones Ejemplo SL");
        s.DomicilioFiscal.Should().Be("Calle Mayor 1");
        s.Tramo.Should().Be(TramoTrabajadoresActivos.De81A150);
        s.DeclaraRepresentacion.Should().BeTrue();
    }

    [Theory]
    [InlineData("B12345675")]
    [InlineData("B١٢٣4٥674")]
    [InlineData("12345678A")]
    [InlineData("ABC123456")]
    [InlineData("")]
    public void Completar_datos_rechaza_un_CIF_o_NIF_invalido(string cif)
    {
        var s = En(EstadoSolicitudAlta.CorreoVerificado);

        var accion = () => s.CompletarDatos("Org", cif, "Calle 1", TramoTrabajadoresActivos.De1A10, true, true, Inicio.AddHours(2));

        accion.Should().Throw<ArgumentException>();
        s.Estado.Should().Be(EstadoSolicitudAlta.CorreoVerificado);
    }

    [Fact]
    public void Sin_declaracion_de_representacion_no_se_completan_los_datos()
    {
        var s = En(EstadoSolicitudAlta.CorreoVerificado);

        var accion = () => s.CompletarDatos("Org", CifEmpresa, "Calle 1", TramoTrabajadoresActivos.De1A10, false, false, Inicio.AddHours(2));

        accion.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("declaraRepresentacion");
    }

    [Theory]
    [InlineData(Dni)]
    [InlineData("1234567L")]
    [InlineData("X1234567L")]
    public void Un_NIF_de_persona_fisica_exige_declarar_la_actividad_profesional(string nif)
    {
        var sin = En(EstadoSolicitudAlta.CorreoVerificado);
        var accion = () => sin.CompletarDatos("Ana Autónoma", nif, "Calle 1", TramoTrabajadoresActivos.De1A10, true, false, Inicio.AddHours(2));
        accion.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("declaraActividadProfesional");

        var con = En(EstadoSolicitudAlta.CorreoVerificado);
        con.CompletarDatos("Ana Autónoma", nif, "Calle 1", TramoTrabajadoresActivos.De1A10, true, true, Inicio.AddHours(2));
        con.DeclaraActividadProfesional.Should().BeTrue();
        SolicitudAlta.EsPersonaFisica(con.CifNif!).Should().BeTrue();
    }

    [Fact]
    public void Un_CIF_de_persona_juridica_no_exige_la_declaracion_de_actividad()
    {
        SolicitudAlta.EsPersonaFisica(CifEmpresa).Should().BeFalse();
        En(EstadoSolicitudAlta.DatosCompletos).DeclaraActividadProfesional.Should().BeFalse();
    }

    [Fact]
    public void Derivar_a_acceso_guarda_el_CIF_normalizado_y_es_final()
    {
        var s = En(EstadoSolicitudAlta.CorreoVerificado);

        s.DerivarAAcceso(" b12345674 ", Inicio.AddHours(2));

        s.Estado.Should().Be(EstadoSolicitudAlta.DerivadaAAcceso);
        s.CifNif.Should().Be(CifEmpresa);
        SolicitudAlta.EsFinal(s.Estado).Should().BeTrue();
    }

    // ── Pago ──

    [Fact]
    public void Pasar_a_pendiente_de_pago_guarda_modalidad_aceptaciones_y_sesion()
    {
        var s = En(EstadoSolicitudAlta.PendientePago);

        s.Modalidad.Should().Be(ModalidadSuscripcionAlta.Anual);
        s.CheckoutSessionId.Should().Be(Sesion);
        var aceptacion = s.Aceptaciones.Should().ContainSingle().Subject;
        aceptacion.SolicitudAltaId.Should().Be(s.Id);
        aceptacion.Documento.Should().Be("Condiciones generales");
        aceptacion.Version.Should().Be("2026-11-01");
        aceptacion.HashSha256.Should().Be(new string('a', 64));
        aceptacion.DeclaraFacultades.Should().BeTrue();
        aceptacion.Ip.Should().Be("203.0.113.7");
        aceptacion.AceptadaEnUtc.Should().Be(Inicio.AddHours(1));
    }

    [Fact]
    public void Sin_aceptaciones_con_un_documento_repetido_o_con_un_hash_invalido_no_se_pasa_a_pago()
    {
        var s = En(EstadoSolicitudAlta.DatosCompletos);
        var t = Inicio.AddHours(2);

        ((Action)(() => s.PasarAPendientePago(ModalidadSuscripcionAlta.Mensual, [], Sesion, t))).Should().Throw<ArgumentException>();
        ((Action)(() => s.PasarAPendientePago(ModalidadSuscripcionAlta.Mensual, [Aceptacion(), Aceptacion()], Sesion, t))).Should().Throw<ArgumentException>();
        ((Action)(() => s.PasarAPendientePago(ModalidadSuscripcionAlta.Mensual, [Aceptacion() with { HashSha256 = "abc" }], Sesion, t))).Should().Throw<ArgumentException>();
        ((Action)(() => s.PasarAPendientePago(ModalidadSuscripcionAlta.Mensual, [Aceptacion()], " ", t))).Should().Throw<ArgumentException>();

        s.Estado.Should().Be(EstadoSolicitudAlta.DatosCompletos);
        s.Aceptaciones.Should().BeEmpty();
    }

    [Fact]
    public void Sin_declaracion_de_facultades_o_con_una_aceptacion_futura_no_se_pasa_a_pago()
    {
        var s = En(EstadoSolicitudAlta.DatosCompletos);
        var t = Inicio.AddHours(2);

        ((Action)(() => s.PasarAPendientePago(ModalidadSuscripcionAlta.Mensual,
            [Aceptacion() with { DeclaraFacultades = false }, Aceptacion("Política de privacidad") with { DeclaraFacultades = false }],
            Sesion, t))).Should().Throw<ArgumentException>().WithMessage("*facultades*");
        ((Action)(() => s.PasarAPendientePago(ModalidadSuscripcionAlta.Mensual,
            [Aceptacion() with { AceptadaEnUtc = t.AddSeconds(1) }], Sesion, t))).Should().Throw<ArgumentException>().WithMessage("*posterior*");
        s.Estado.Should().Be(EstadoSolicitudAlta.DatosCompletos);

        s.PasarAPendientePago(ModalidadSuscripcionAlta.Mensual,
            [Aceptacion(), Aceptacion("Política de privacidad") with { DeclaraFacultades = false, AceptadaEnUtc = t }], Sesion, t);
        s.Estado.Should().Be(EstadoSolicitudAlta.PendientePago, "basta con que una aceptación declare facultades");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData(" +34 977 123 456 ", "+34 977 123 456")]
    [InlineData("(977) 12-34.56", "(977) 12-34.56")]
    public void El_telefono_es_opcional_y_se_guarda_recortado(string? telefono, string? esperado)
    {
        var s = En(EstadoSolicitudAlta.CorreoVerificado);

        s.CompletarDatos("Org", CifEmpresa, "Calle 1", TramoTrabajadoresActivos.De1A10, true, false, Inicio.AddHours(2), telefono);

        s.Telefono.Should().Be(esperado);
    }

    [Theory]
    [InlineData("977abc123")]
    [InlineData("+-() .")]
    [InlineData("1234567890123456789012345678901")]
    public void Un_telefono_con_otros_caracteres_sin_digitos_o_demasiado_largo_se_rechaza(string telefono)
    {
        var s = En(EstadoSolicitudAlta.CorreoVerificado);

        var accion = () => s.CompletarDatos("Org", CifEmpresa, "Calle 1", TramoTrabajadoresActivos.De1A10, true, false, Inicio.AddHours(2), telefono);

        accion.Should().Throw<ArgumentException>().WithParameterName("telefono");
        s.Estado.Should().Be(EstadoSolicitudAlta.CorreoVerificado);
    }

    [Fact]
    public void Completar_descarta_la_copia_del_hash_de_la_contrasena()
    {
        var s = En(EstadoSolicitudAlta.Pagada);
        s.HashContrasena.Should().NotBeNull("guarda del instrumento: hasta Pagada el hash sigue en la Solicitud de alta");

        s.Completar(Guid.NewGuid(), Inicio.AddHours(5));

        s.HashContrasena.Should().BeNull();
    }

    [Fact]
    public void Un_pago_rechazado_no_deja_el_agregado_a_medias()
    {
        var s = En(EstadoSolicitudAlta.PendientePago);
        var version = s.VersionFila;

        ((Action)(() => s.MarcarPagada("cs_otra", "cus_1", "sub_1", Inicio.AddHours(4)))).Should().Throw<ArgumentException>(
            "solo se acepta la sesión de Checkout de esta Solicitud de alta");
        ((Action)(() => s.MarcarPagada(Sesion, "cus_1", " ", Inicio.AddHours(4)))).Should().Throw<ArgumentException>()
            .WithParameterName("stripeSubscriptionId");

        s.Estado.Should().Be(EstadoSolicitudAlta.PendientePago);
        s.StripeCustomerId.Should().BeNull("el customer válido no se asigna si la suscripción no lo es");
        s.StripeSubscriptionId.Should().BeNull();
        s.VersionFila.Should().Be(version);

        s.MarcarPagada(Sesion, "cus_1", "sub_1", Inicio.AddHours(4));
        s.Estado.Should().Be(EstadoSolicitudAlta.Pagada);
    }

    [Fact]
    public void El_correo_normalizado_sigue_el_criterio_de_Identity()
    {
        var compuesto = SolicitudAlta.Iniciar("josé@ejemplo.es", "hash", Token, "203.0.113.7", null, Inicio);
        var descompuesto = SolicitudAlta.Iniciar("josé@ejemplo.es", "hash", Token, "203.0.113.7", null, Inicio);

        descompuesto.CorreoNormalizado.Should().Be(compuesto.CorreoNormalizado, "dos formas Unicode del mismo correo son el mismo correo");
        compuesto.CorreoNormalizado.Should().Be("JOSÉ@EJEMPLO.ES");
        descompuesto.Correo.Should().Be("josé@ejemplo.es", "el original se guarda tal cual");
    }

    [Fact]
    public void Marcar_pagada_guarda_los_ids_de_Stripe_aunque_haya_vencido_el_plazo()
    {
        var s = En(EstadoSolicitudAlta.PendientePago);

        s.MarcarPagada(Sesion, "cus_1", "sub_1", s.CaducaEnUtc.AddMinutes(5));

        s.Estado.Should().Be(EstadoSolicitudAlta.Pagada);
        s.StripeCustomerId.Should().Be("cus_1");
        s.StripeSubscriptionId.Should().Be("sub_1");
    }

    [Fact]
    public void Completar_guarda_el_Tenant_creado_y_reembolsar_el_motivo()
    {
        var tenant = Guid.NewGuid();
        var completada = En(EstadoSolicitudAlta.Pagada);
        completada.Completar(tenant, Inicio.AddHours(5));
        completada.Estado.Should().Be(EstadoSolicitudAlta.Completada);
        completada.TenantIdCreado.Should().Be(tenant);

        var reembolsada = En(EstadoSolicitudAlta.Pagada);
        reembolsada.Reembolsar("  CIF ocupado  ", Inicio.AddHours(5));
        reembolsada.Estado.Should().Be(EstadoSolicitudAlta.Reembolsada);
        reembolsada.Motivo.Should().Be("CIF ocupado");

        ((Action)(() => En(EstadoSolicitudAlta.Pagada).Completar(Guid.Empty, Inicio))).Should().Throw<ArgumentException>();
        ((Action)(() => En(EstadoSolicitudAlta.Pagada).Reembolsar(" ", Inicio))).Should().Throw<ArgumentException>();
    }

    // ── Matriz de transiciones: cada una solo desde sus estados ──

    private static readonly Dictionary<string, (EstadoSolicitudAlta[] Desde, Action<SolicitudAlta, DateTime> Hacer)> Transiciones = new()
    {
        ["RenovarTokenVerificacion"] = ([EstadoSolicitudAlta.Iniciada], (s, t) => s.RenovarTokenVerificacion(new string('n', 43), t)),
        ["VerificarCorreo"] = ([EstadoSolicitudAlta.Iniciada], (s, t) => s.VerificarCorreo(Token, t)),
        ["CompletarDatos"] = ([EstadoSolicitudAlta.CorreoVerificado],
            (s, t) => s.CompletarDatos("Org", CifEmpresa, "Calle 1", TramoTrabajadoresActivos.De1A10, true, false, t)),
        ["DerivarAAcceso"] = ([EstadoSolicitudAlta.CorreoVerificado], (s, t) => s.DerivarAAcceso(CifEmpresa, t)),
        ["PasarAPendientePago"] = ([EstadoSolicitudAlta.DatosCompletos],
            (s, t) => s.PasarAPendientePago(ModalidadSuscripcionAlta.Mensual, [Aceptacion("Otro documento")], "cs_test_9", t)),
        ["MarcarPagada"] = ([EstadoSolicitudAlta.PendientePago], (s, t) => s.MarcarPagada(Sesion, "cus_9", "sub_9", t)),
        ["Completar"] = ([EstadoSolicitudAlta.Pagada], (s, t) => s.Completar(Guid.NewGuid(), t)),
        ["Reembolsar"] = ([EstadoSolicitudAlta.Pagada], (s, t) => s.Reembolsar("motivo", t)),
        ["Caducar"] = ([EstadoSolicitudAlta.Iniciada, EstadoSolicitudAlta.CorreoVerificado, EstadoSolicitudAlta.DatosCompletos,
            EstadoSolicitudAlta.PendientePago], (s, _) => s.Caducar(s.CaducaEnUtc)),
        ["Bloquear"] = ([EstadoSolicitudAlta.Iniciada, EstadoSolicitudAlta.CorreoVerificado, EstadoSolicitudAlta.DatosCompletos,
            EstadoSolicitudAlta.PendientePago, EstadoSolicitudAlta.Pagada], (s, t) => s.Bloquear("motivo", t)),
        ["BorrarDatosPersonales"] = ([EstadoSolicitudAlta.Caducada], (s, t) => s.BorrarDatosPersonales(t)),
    };

    public static TheoryData<string, EstadoSolicitudAlta> TransicionYEstado()
    {
        var datos = new TheoryData<string, EstadoSolicitudAlta>();
        foreach (var transicion in Transiciones.Keys)
            foreach (var estado in Enum.GetValues<EstadoSolicitudAlta>())
                datos.Add(transicion, estado);
        return datos;
    }

    [Theory]
    [MemberData(nameof(TransicionYEstado))]
    public void Cada_transicion_solo_se_admite_desde_sus_estados(string transicion, EstadoSolicitudAlta estado)
    {
        var (desde, hacer) = Transiciones[transicion];
        var s = En(estado);
        var antes = s.Estado;
        var t = Inicio.AddHours(3);

        var accion = () => hacer(s, t);

        if (desde.Contains(estado))
        {
            accion.Should().NotThrow();
        }
        else
        {
            accion.Should().Throw<TransicionSolicitudAltaNoPermitidaException>()
                .Which.Desde.Should().Be(estado);
            s.Estado.Should().Be(antes);
        }
    }

    [Fact]
    public void La_matriz_cubre_todos_los_metodos_publicos_que_cambian_la_solicitud()
    {
        var metodos = typeof(SolicitudAlta).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                                                       | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.Name != nameof(SolicitudAlta.EstaVencida))
            .Select(m => m.Name)
            .ToHashSet();

        metodos.Should().BeEquivalentTo(Transiciones.Keys,
            "una transición nueva sin fila en la matriz no tendría probados sus estados prohibidos");
    }

    [Theory]
    [MemberData(nameof(TodosLosEstados))]
    public void Los_estados_finales_son_los_del_contrato(EstadoSolicitudAlta estado)
    {
        var finales = new[]
        {
            EstadoSolicitudAlta.DerivadaAAcceso, EstadoSolicitudAlta.Completada, EstadoSolicitudAlta.Reembolsada,
            EstadoSolicitudAlta.Caducada, EstadoSolicitudAlta.Bloqueada,
        };

        SolicitudAlta.EsFinal(estado).Should().Be(finales.Contains(estado));
    }

    // ── Caducidad ──

    [Fact]
    public void Caducar_antes_del_plazo_se_rechaza()
    {
        var s = Iniciada();

        var accion = () => s.Caducar(s.CaducaEnUtc.AddTicks(-1));

        accion.Should().Throw<InvalidOperationException>();
        s.Estado.Should().Be(EstadoSolicitudAlta.Iniciada);
        s.EstaVencida(s.CaducaEnUtc.AddTicks(-1)).Should().BeFalse();
        s.EstaVencida(s.CaducaEnUtc).Should().BeTrue();
    }

    [Theory]
    [InlineData(EstadoSolicitudAlta.Iniciada)]
    [InlineData(EstadoSolicitudAlta.CorreoVerificado)]
    [InlineData(EstadoSolicitudAlta.DatosCompletos)]
    public void Vencido_el_plazo_solo_admite_caducar_o_bloquearse(EstadoSolicitudAlta estado)
    {
        var s = En(estado);
        var vencida = s.CaducaEnUtc;

        var avanzar = estado switch
        {
            EstadoSolicitudAlta.Iniciada => () => s.VerificarCorreo(Token, vencida),
            EstadoSolicitudAlta.CorreoVerificado => () => s.CompletarDatos("Org", CifEmpresa, "Calle 1", TramoTrabajadoresActivos.De1A10, true, false, vencida),
            EstadoSolicitudAlta.DatosCompletos => (Action)(() => s.PasarAPendientePago(ModalidadSuscripcionAlta.Mensual, [Aceptacion()], Sesion, vencida)),
            _ => throw new ArgumentOutOfRangeException(nameof(estado)),
        };

        avanzar.Should().Throw<InvalidOperationException>();
        s.Estado.Should().Be(estado);

        s.Caducar(vencida);
        s.Estado.Should().Be(EstadoSolicitudAlta.Caducada);
    }

    // ── Borrado de datos personales ──

    [Fact]
    public void Borrar_datos_personales_deja_solo_estado_fechas_tramo_y_modalidad()
    {
        var s = En(EstadoSolicitudAlta.PendientePago);
        s.Caducar(s.CaducaEnUtc);
        var borrado = s.CaducaEnUtc.AddDays(23);

        s.Telefono.Should().NotBeNull("guarda del instrumento: el camino de la prueba guarda un teléfono");

        s.BorrarDatosPersonales(borrado);

        s.Correo.Should().BeNull();
        s.Telefono.Should().BeNull();
        s.CorreoNormalizado.Should().BeNull();
        s.HashContrasena.Should().BeNull();
        s.HashTokenVerificacion.Should().BeNull();
        s.IpOrigen.Should().BeNull();
        s.UserAgentOrigen.Should().BeNull();
        s.RazonSocial.Should().BeNull();
        s.CifNif.Should().BeNull();
        s.DomicilioFiscal.Should().BeNull();
        s.CheckoutSessionId.Should().BeNull();
        s.Aceptaciones.Should().BeEmpty();
        s.DatosPersonalesBorradosEnUtc.Should().Be(borrado);

        s.Estado.Should().Be(EstadoSolicitudAlta.Caducada);
        s.CreadaEnUtc.Should().Be(Inicio);
        s.Tramo.Should().Be(TramoTrabajadoresActivos.De11A40);
        s.Modalidad.Should().Be(ModalidadSuscripcionAlta.Anual);
    }

    [Fact]
    public void Borrar_dos_veces_no_cambia_la_primera_fecha_ni_la_version()
    {
        var s = En(EstadoSolicitudAlta.Caducada);
        s.BorrarDatosPersonales(Inicio.AddDays(30));
        var version = s.VersionFila;

        s.BorrarDatosPersonales(Inicio.AddDays(31));

        s.DatosPersonalesBorradosEnUtc.Should().Be(Inicio.AddDays(30));
        s.VersionFila.Should().Be(version);
    }

    [Fact]
    public void Cada_transicion_incrementa_la_version_de_fila()
    {
        var s = Iniciada();
        var versiones = new List<int> { s.VersionFila };
        var t = Inicio.AddHours(1);

        s.VerificarCorreo(Token, t);
        versiones.Add(s.VersionFila);
        s.CompletarDatos("Org", CifEmpresa, "Calle 1", TramoTrabajadoresActivos.De1A10, true, false, t);
        versiones.Add(s.VersionFila);
        s.PasarAPendientePago(ModalidadSuscripcionAlta.Mensual, [Aceptacion()], Sesion, t);
        versiones.Add(s.VersionFila);
        s.MarcarPagada(Sesion, "cus_1", "sub_1", t);
        versiones.Add(s.VersionFila);
        s.Completar(Guid.NewGuid(), t);
        versiones.Add(s.VersionFila);

        versiones.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        s.UltimoCambioEstadoEnUtc.Should().Be(t);
    }
}
