using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// <b>Línea base compactada</b> (P1-M3): sustituye a las 185 migraciones de
    /// <c>20260731235023_LineaBase</c> a
    /// <c>20260926133410_CorregirBackfillReclamacionBuzonIntegracionBajoRls</c> y crea
    /// desde cero exactamente el esquema que dejaban —tablas, índices, restricciones,
    /// RLS y políticas, GRANT/REVOKE y privilegios por defecto, funciones, triggers,
    /// extensiones, el esquema <c>app_privado</c> y el particionado de auditoría—, con
    /// las mismas filas sembradas. Se comprobó con el diff de <c>pg_dump --schema-only</c>
    /// y <c>--data-only</c> entre una base migrada con el historial previo y otra con
    /// esta línea base: idénticos.
    ///
    /// <para>
    /// Una base que ya tenía aplicadas las 185 no ejecuta esta migración: el migrador
    /// reescribe su <c>__EFMigrationsHistory</c> antes de <c>MigrateAsync</c>
    /// (<c>TransicionLineaBaseCompactada</c>, en Infrastructure).
    /// </para>
    ///
    /// <para>
    /// Sigue exigiendo lo mismo que el historial previo: los roles de clúster de
    /// <c>deploy/bootstrap/roles-de-cluster.sql</c> ya creados (ninguna migración
    /// los crea) y un migrador superusuario o con BYPASSRLS (lo exige el
    /// particionado).
    /// </para>
    /// </summary>
    public partial class LineaBaseCompactada : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Esquema completo salvo las particiones de auditoría (ver EsquemaSql).
            migrationBuilder.Sql(EsquemaSql);

            // 2. Filas sembradas del modelo (HasData), tal como las genera EF.
            migrationBuilder.InsertData(
                table: "AspNetRoles",
                columns: new[] { "Id", "ConcurrencyStamp", "Name", "NormalizedName" },
                values: new object[,]
                {
                    { new Guid("30000000-0000-0000-0000-000000000001"), "30000000-0000-0000-0000-000000000001", "Administrador", "ADMINISTRADOR" },
                    { new Guid("30000000-0000-0000-0000-000000000002"), "30000000-0000-0000-0000-000000000002", "CoordinadorCae", "COORDINADORCAE" },
                    { new Guid("30000000-0000-0000-0000-000000000003"), "30000000-0000-0000-0000-000000000003", "GestorCae", "GESTORCAE" },
                    { new Guid("30000000-0000-0000-0000-000000000004"), "30000000-0000-0000-0000-000000000004", "Consulta", "CONSULTA" },
                    { new Guid("30000000-0000-0000-0000-000000000005"), "30000000-0000-0000-0000-000000000005", "DireccionCae", "DIRECCIONCAE" },
                    { new Guid("30000000-0000-0000-0000-000000000006"), "30000000-0000-0000-0000-000000000006", "Cliente", "CLIENTE" }
                });

            migrationBuilder.InsertData(
                table: "ConocimientosDeteccionCampo",
                columns: new[] { "Id", "EtiquetaNormalizada", "FuenteDatoCandidata", "Prioridad" },
                values: new object[,]
                {
                    { new Guid("6a000000-0000-0000-0000-000000000001"), "razon social", "EmpresaRazonSocial", 100 },
                    { new Guid("6a000000-0000-0000-0000-000000000002"), "empresa", "EmpresaRazonSocial", 50 },
                    { new Guid("6a000000-0000-0000-0000-000000000003"), "nombre de la empresa", "EmpresaRazonSocial", 100 },
                    { new Guid("6a000000-0000-0000-0000-000000000004"), "cif", "EmpresaCif", 100 },
                    { new Guid("6a000000-0000-0000-0000-000000000005"), "cif empresa", "EmpresaCif", 100 },
                    { new Guid("6a000000-0000-0000-0000-000000000006"), "nif", "EmpresaCif", 80 },
                    { new Guid("6a000000-0000-0000-0000-000000000007"), "nombre y apellidos", "TrabajadorNombreCompleto", 100 },
                    { new Guid("6a000000-0000-0000-0000-000000000008"), "nombre completo", "TrabajadorNombreCompleto", 90 },
                    { new Guid("6a000000-0000-0000-0000-000000000009"), "trabajador", "TrabajadorNombreCompleto", 50 },
                    { new Guid("6a00000a-0000-0000-0000-000000000001"), "nombre del trabajador", "TrabajadorNombreCompleto", 100 },
                    { new Guid("6a00000a-0000-0000-0000-000000000002"), "dni", "TrabajadorDni", 100 },
                    { new Guid("6a00000a-0000-0000-0000-000000000003"), "dni trabajador", "TrabajadorDni", 100 },
                    { new Guid("6a00000a-0000-0000-0000-000000000004"), "documento de identidad", "TrabajadorDni", 80 },
                    { new Guid("6a00000a-0000-0000-0000-000000000005"), "puesto", "TrabajadorPuesto", 100 },
                    { new Guid("6a00000a-0000-0000-0000-000000000006"), "puesto de trabajo", "TrabajadorPuesto", 100 },
                    { new Guid("6a00000a-0000-0000-0000-000000000007"), "categoria profesional", "TrabajadorPuesto", 70 },
                    { new Guid("6a00000a-0000-0000-0000-000000000008"), "centro de trabajo", "CentroNombre", 100 },
                    { new Guid("6a00000a-0000-0000-0000-000000000009"), "centro", "CentroNombre", 60 },
                    { new Guid("6a00000b-0000-0000-0000-000000000001"), "direccion del centro", "CentroDireccion", 100 },
                    { new Guid("6a00000b-0000-0000-0000-000000000002"), "direccion", "CentroDireccion", 60 },
                    { new Guid("6a00000b-0000-0000-0000-000000000003"), "cliente", "ClienteRazonSocial", 50 },
                    { new Guid("6a00000b-0000-0000-0000-000000000004"), "fecha", "DocumentoFechaGeneracion", 60 },
                    { new Guid("6a00000b-0000-0000-0000-000000000005"), "responsable de prl", "EmpresaResponsablePrl", 100 },
                    { new Guid("6a00000b-0000-0000-0000-000000000006"), "responsable prl", "EmpresaResponsablePrl", 100 },
                    { new Guid("6a00000b-0000-0000-0000-000000000007"), "representante legal", "EmpresaRepresentanteLegal", 100 },
                    { new Guid("6a00000b-0000-0000-0000-000000000008"), "contacto cae", "EmpresaContactoCae", 100 }
                });

            migrationBuilder.InsertData(
                table: "DominiosProveedorPlataformaCae",
                columns: new[] { "Id", "Dominio", "ProveedorPlataformaCaeId" },
                values: new object[,]
                {
                    { new Guid("70000000-0000-0000-0000-000000000001"), "nalandaglobal.com", new Guid("60000000-0000-0000-0000-000000000001") },
                    { new Guid("70000000-0000-0000-0000-000000000002"), "dokify.net", new Guid("60000000-0000-0000-0000-000000000002") },
                    { new Guid("70000000-0000-0000-0000-000000000003"), "app.twind.io", new Guid("60000000-0000-0000-0000-000000000003") },
                    { new Guid("70000000-0000-0000-0000-000000000004"), "ctaimacae.net", new Guid("60000000-0000-0000-0000-000000000004") },
                    { new Guid("70000000-0000-0000-0000-000000000005"), "e-coordina.es", new Guid("60000000-0000-0000-0000-000000000005") },
                    { new Guid("70000000-0000-0000-0000-000000000006"), "metacontratas.com", new Guid("60000000-0000-0000-0000-000000000006") },
                    { new Guid("70000000-0000-0000-0000-000000000007"), "adding-plus.com", new Guid("60000000-0000-0000-0000-000000000007") },
                    { new Guid("70000000-0000-0000-0000-000000000008"), "ucae.es", new Guid("60000000-0000-0000-0000-000000000008") },
                    { new Guid("70000000-0000-0000-0000-000000000009"), "validate.es", new Guid("60000000-0000-0000-0000-000000000009") },
                    { new Guid("70000000-0001-0000-0000-000000000003"), "ctaima.com", new Guid("60000000-0000-0000-0000-000000000003") },
                    { new Guid("70000000-0001-0000-0000-000000000007"), "coordinaplus.net", new Guid("60000000-0000-0000-0000-000000000007") },
                    { new Guid("70000000-0001-0000-0000-000000000009"), "validate.network", new Guid("60000000-0000-0000-0000-000000000009") },
                    { new Guid("7000000a-0000-0000-0000-000000000001"), "egestiona.com", new Guid("6000000a-0000-0000-0000-000000000001") },
                    { new Guid("7000000a-0000-0000-0000-000000000002"), "smartosh.com", new Guid("6000000a-0000-0000-0000-000000000002") },
                    { new Guid("7000000a-0000-0000-0000-000000000003"), "ecogestor.com", new Guid("6000000a-0000-0000-0000-000000000003") },
                    { new Guid("7000000a-0000-0000-0000-000000000004"), "sabentis.com", new Guid("6000000a-0000-0000-0000-000000000004") },
                    { new Guid("7000000a-0000-0000-0000-000000000005"), "unifikas.com", new Guid("6000000a-0000-0000-0000-000000000005") },
                    { new Guid("7000000a-0000-0000-0000-000000000006"), "quironprevencion.com", new Guid("6000000a-0000-0000-0000-000000000006") },
                    { new Guid("7000000a-0000-0000-0000-000000000007"), "previntegral.com", new Guid("6000000a-0000-0000-0000-000000000007") },
                    { new Guid("7000000a-0000-0000-0000-000000000008"), "vithas.es", new Guid("6000000a-0000-0000-0000-000000000008") },
                    { new Guid("7000000a-0000-0000-0000-000000000009"), "ergasia.es", new Guid("6000000a-0000-0000-0000-000000000009") },
                    { new Guid("7000000a-0001-0000-0000-000000000001"), "egestiona.es", new Guid("6000000a-0000-0000-0000-000000000001") },
                    { new Guid("7000000a-0001-0000-0000-000000000004"), "quironprevencion.com", new Guid("6000000a-0000-0000-0000-000000000004") },
                    { new Guid("7000000b-0000-0000-0000-000000000001"), "valoraprevencion.es", new Guid("6000000b-0000-0000-0000-000000000001") },
                    { new Guid("7000000b-0000-0000-0000-000000000002"), "playcae.com", new Guid("6000000b-0000-0000-0000-000000000002") },
                    { new Guid("7000000b-0000-0000-0000-000000000004"), "archbus.com", new Guid("6000000b-0000-0000-0000-000000000004") },
                    { new Guid("7000000b-0000-0000-0000-000000000005"), "opground.com", new Guid("6000000b-0000-0000-0000-000000000005") }
                });

            migrationBuilder.InsertData(
                table: "ParametrosSistema",
                columns: new[] { "Id", "ExcluirFueraDeJornadaEnMetricas", "HoraFinJornada", "HoraInicioJornada", "HorasAvisoVisita", "HorasCriticasVisita", "HorasJornadaMensualGestor", "MedicionTiempoActiva", "PresupuestoMensualIaUsd", "SegundosInactividadPausa", "TenantId", "UmbralAmbarDias", "UmbralRojoDias" },
                values: new object[] { new Guid("20000000-0000-0000-0000-000000000001"), true, new TimeOnly(18, 0, 0), new TimeOnly(8, 0, 0), 48, 24, 160, false, null, 120, new Guid("00000000-0000-0000-0000-000000000001"), 30, 15 });

            migrationBuilder.InsertData(
                table: "ProveedoresPlataformaCae",
                columns: new[] { "Id", "Activo", "Codigo", "Grupo", "Nombre" },
                values: new object[,]
                {
                    { new Guid("60000000-0000-0000-0000-000000000001"), true, "nalanda", "Once For All", "Nalanda" },
                    { new Guid("60000000-0000-0000-0000-000000000002"), true, "dokify", "Once For All", "Dokify" },
                    { new Guid("60000000-0000-0000-0000-000000000003"), true, "twind", "Twind (CTAIMA Group)", "Twind" },
                    { new Guid("60000000-0000-0000-0000-000000000004"), true, "ctaimacae-legacy", "Twind (CTAIMA Group)", "CTAIMACAE (legacy)" },
                    { new Guid("60000000-0000-0000-0000-000000000005"), true, "e-coordina", "Twind (CTAIMA Group)", "e-coordina" },
                    { new Guid("60000000-0000-0000-0000-000000000006"), true, "metacontratas", null, "Metacontratas" },
                    { new Guid("60000000-0000-0000-0000-000000000007"), true, "coordinaplus", "Addingplus", "CoordinaPlus" },
                    { new Guid("60000000-0000-0000-0000-000000000008"), true, "ucae", null, "UCAE" },
                    { new Guid("60000000-0000-0000-0000-000000000009"), true, "validate", null, "Validate" },
                    { new Guid("6000000a-0000-0000-0000-000000000001"), true, "egestiona", null, "eGestiona" },
                    { new Guid("6000000a-0000-0000-0000-000000000002"), true, "smartosh", "Prevencontrol", "SmartOSH" },
                    { new Guid("6000000a-0000-0000-0000-000000000003"), true, "ecogestor", "Eurofins", "EcoGestor" },
                    { new Guid("6000000a-0000-0000-0000-000000000004"), true, "sabentis", null, "Sabentis" },
                    { new Guid("6000000a-0000-0000-0000-000000000005"), true, "unifikas", null, "Unifikas" },
                    { new Guid("6000000a-0000-0000-0000-000000000006"), true, "quiron-prevencion", "Quirónprevención", "Quirón Prevención" },
                    { new Guid("6000000a-0000-0000-0000-000000000007"), true, "previntegral", null, "Previntegral" },
                    { new Guid("6000000a-0000-0000-0000-000000000008"), true, "norprevencion", "Vithas", "Norprevención" },
                    { new Guid("6000000a-0000-0000-0000-000000000009"), true, "ergasia", null, "Ergasia" },
                    { new Guid("6000000b-0000-0000-0000-000000000001"), true, "valora", null, "Valora" },
                    { new Guid("6000000b-0000-0000-0000-000000000002"), true, "playcae", null, "PlayCAE" },
                    { new Guid("6000000b-0000-0000-0000-000000000003"), true, "docuprl", null, "DocuPRL" },
                    { new Guid("6000000b-0000-0000-0000-000000000004"), false, "arch", null, "Arch" },
                    { new Guid("6000000b-0000-0000-0000-000000000005"), false, "opground", null, "Opground" }
                });

            migrationBuilder.InsertData(
                table: "Tenants",
                columns: new[] { "Id", "CreadoEnUtc", "DatosDemoCompletadosEnUtc", "EsPlataforma", "Estado", "EstadoComercial", "EstadoComercialActualizadoEnUtc", "Nombre", "PerfilVocabulario", "PuedeActuarComoOperadorCaeExterno", "StripeCustomerId", "StripeSubscriptionId" },
                values: new object[] { new Guid("00000000-0000-0000-0000-000000000001"), new DateTime(2026, 7, 23, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "Activo", "SinSuscripcion", null, "Organización principal", "ClienteDirecto", false, null, null });

            migrationBuilder.InsertData(
                table: "TiposDocumento",
                columns: new[] { "Id", "AmbitoAplicacion", "AplicaVencimientoAutomatico", "CriteriosValidacion", "Descripcion", "DeteccionTrabajadoresActiva", "LecturaIaActiva", "Naturaleza", "Nombre", "Notas", "Observaciones", "Orden", "PerfilDocumentoOficial", "Requerido", "SeSolicitaA", "Sensibilidad", "TenantId", "VerificacionIaActiva", "VigenciaMeses" },
                values: new object[,]
                {
                    { new Guid("10000000-0000-0000-0000-000000000001"), "Trabajador", true, null, null, false, true, "ObligacionCondicionada", "Certificado de aptitud médica", "Renovación anual estándar. Obligatorio por defecto (2026-08-09): sí o sí exigido en CAE.", null, 1, "Ninguno", "Si", null, "CategoriaEspecialSalud", new Guid("00000000-0000-0000-0000-000000000001"), false, 12 },
                    { new Guid("10000000-0000-0000-0000-000000000002"), "Trabajador", true, null, null, false, true, "PracticaSector", "Entrega de EPI", "Se firman cada año según nota de origen. Obligatorio por defecto (2026-08-09): justificante de entrega de EPIs.", null, 2, "Ninguno", "Si", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, 12 },
                    { new Guid("10000000-0000-0000-0000-000000000003"), "Trabajador", true, null, null, false, true, "RequisitoCliente", "Reciclaje 4h", "Cada 4 años, según Dpto. Formación.", null, 3, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, 48 },
                    { new Guid("10000000-0000-0000-0000-000000000004"), "Trabajador", true, null, null, false, true, "ObligacionLegal", "Formación Art. 19", "Recordatorio cada 3 años. Obligatorio por defecto (2026-08-09): formación PRL Art. 19.", null, 4, "Ninguno", "Si", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, 36 },
                    { new Guid("10000000-0000-0000-0000-000000000005"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Formación 60h (base convenio)", "Formación base, no consta caducidad.", null, 5, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("10000000-0000-0000-0000-000000000006"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Formación 20h", "Mismo curso de convenio que 60h/6h, no consta caducidad.", null, 6, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("10000000-0000-0000-0000-000000000007"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Formación 6h", "Mismo curso de convenio que 60h/20h, no consta caducidad.", null, 7, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("10000000-0000-0000-0000-000000000008"), "Trabajador", false, null, null, false, true, "ObligacionLegal", "Información Art. 18", "No consta periodicidad de renovación. Obligatorio por defecto (2026-08-09): registro de entrega de información de riesgos del puesto.", null, 8, "Ninguno", "Si", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("10000000-0000-0000-0000-000000000009"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Carretillas elevadoras", "Configurable si el convenio interno define vigencia.", null, 9, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("10000000-0000-0000-0000-00000000000a"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "PEMP (plataformas elevadoras)", "Configurable si el convenio interno define vigencia.", null, 10, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("10000000-0000-0000-0000-00000000000b"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "LOTO (4h)", "Configurable si el convenio interno define vigencia.", null, 11, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("10000000-0000-0000-0000-00000000000c"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Seguridad alimentaria", "Configurable si el convenio interno define vigencia.", null, 12, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("10000000-0000-0000-0000-00000000000d"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Primeros auxilios", "Se recomienda revisar cada 2 años; sin dato oficial de origen.", null, 13, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("10000000-0000-0000-0000-00000000000e"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Espacios confinados", "Configurable si el convenio interno define vigencia.", null, 14, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("10000000-0000-0000-0000-00000000000f"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Trabajos en altura (8h)", "Configurable si el convenio interno define vigencia.", null, 15, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("20000000-0000-0000-0000-000000000001"), "Empresa", true, null, null, false, true, "PracticaSector", "Certificado de estar al corriente con la Seguridad Social", "Mensual.", null, 16, "CorrienteTgss", "Si", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, 1 },
                    { new Guid("20000000-0000-0000-0000-000000000002"), "Empresa", false, null, null, false, true, "ObligacionCondicionada", "Certificado de estar al corriente con Hacienda", "Vigencia variable (1, 3, 6 o 12 meses según lo que exija el cliente) — la fecha de vencimiento se introduce a mano al subir el documento.", null, 17, "CorrienteAeat", "Si", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("20000000-0000-0000-0000-000000000003"), "Empresa", true, null, null, true, true, "RequisitoCliente", "ITA", "Mensual.", null, 18, "Ita", "Si", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, 1 },
                    { new Guid("20000000-0000-0000-0000-000000000004"), "Empresa", true, null, null, false, true, "RequisitoCliente", "RLC", "Mensual — el documento de un periodo (p. ej. 01/05) vence 3 meses después (01/08), porque tarda en emitirse con la fecha del periodo ya pasada.", null, 19, "Rlc", "Si", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, 3 },
                    { new Guid("20000000-0000-0000-0000-000000000005"), "Empresa", true, null, null, false, true, "RequisitoCliente", "Recibo de pago RLC/TC1", "Mismo criterio de vigencia que el RLC.", null, 20, "Ninguno", "Si", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, 3 },
                    { new Guid("20000000-0000-0000-0000-000000000006"), "Empresa", true, null, null, false, true, "RequisitoCliente", "RLC/TC1 + Recibo de pago", "Variante combinada — mismo criterio de vigencia que el RLC.", null, 21, "Rlc", "Si", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, 3 },
                    { new Guid("20000000-0000-0000-0000-000000000007"), "Empresa", true, null, null, true, true, "RequisitoCliente", "RNT", "Mismo criterio que el RLC.", null, 22, "Rnt", "Si", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, 3 },
                    { new Guid("20000000-0000-0000-0000-000000000008"), "Empresa", false, null, null, false, true, "RequisitoCliente", "Mutua", "Vigencia sin especificar — fecha de vencimiento manual.", null, 23, "Ninguno", "No", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("20000000-0000-0000-0000-000000000009"), "Empresa", false, null, null, false, true, "RequisitoCliente", "Seguro de Responsabilidad Civil + recibo de pago", "Vigencia sin especificar — fecha de vencimiento manual.", null, 24, "Ninguno", "Si", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("2000000a-0000-0000-0000-00000000000a"), "Empresa", false, null, null, false, true, "PracticaSector", "Servicio de Prevención Ajeno", "Debe venir acompañado de un certificado de pago que indica la fecha fin de validez — se introduce esa fecha manualmente.", null, 25, "Ninguno", "Si", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("2000000b-0000-0000-0000-00000000000b"), "Empresa", false, null, null, false, true, "ObligacionLegal", "Evaluación de Riesgos Laborales", "Vigencia sin especificar — fecha de vencimiento manual.", null, 26, "Ninguno", "Si", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("2000000c-0000-0000-0000-00000000000c"), "Empresa", false, null, null, false, true, "ObligacionLegal", "Planificación de la Actividad Preventiva", "Vigencia sin especificar — fecha de vencimiento manual.", null, 27, "Ninguno", "Si", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("2000000d-0000-0000-0000-00000000000d"), "Empresa", false, null, null, false, true, "RequisitoCliente", "Tarjeta de identificación fiscal", "Opcional — no obligatorio para todos los clientes.", null, 28, "Ninguno", "No", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("30000000-0000-0000-0000-000000000001"), "Vehiculo", true, null, null, false, true, "RequisitoCliente", "ITC", "Vigencia anual.", null, 1, "Ninguno", "Si", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, 12 },
                    { new Guid("30000000-0000-0000-0000-000000000002"), "Vehiculo", true, null, null, false, true, "RequisitoCliente", "Ficha técnica", "Vigencia anual.", null, 2, "Ninguno", "Si", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, 12 },
                    { new Guid("30000000-0000-0000-0000-000000000003"), "Vehiculo", true, null, null, false, true, "RequisitoCliente", "Seguro", "Vigencia anual.", null, 3, "Ninguno", "Si", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, 12 },
                    { new Guid("30000000-0000-0000-0000-000000000004"), "Vehiculo", true, null, null, false, true, "RequisitoCliente", "Autorización de circulación", "Vigencia anual.", null, 4, "Ninguno", "Si", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, 12 },
                    { new Guid("40000000-0000-0000-0000-000000000001"), "Empresa", false, null, null, false, true, "ObligacionLegal", "Plan de Prevención", "Vigente con revisiones — vencimiento manual.", null, 29, "Ninguno", "Si", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("40000000-0000-0000-0000-000000000002"), "Empresa", false, null, null, false, true, "ObligacionCondicionada", "Designación de Recursos Preventivos", "Vigente hasta modificación — vencimiento manual.", null, 30, "Ninguno", "Condicional", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("40000000-0000-0000-0000-000000000003"), "Empresa", false, null, null, false, true, "ObligacionCondicionada", "Procedimiento de Coordinación de Actividades Empresariales", "Vigente hasta revisión — vencimiento manual.", null, 31, "Ninguno", "Condicional", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("40000000-0000-0000-0000-000000000004"), "Empresa", false, null, null, false, true, "RequisitoCliente", "Política Preventiva", "Vigente hasta revisión — vencimiento manual.", null, 32, "Ninguno", "No", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("40000000-0000-0000-0000-000000000005"), "Empresa", false, null, null, false, true, "RequisitoCliente", "Organigrama Preventivo", "Vigente hasta cambios — vencimiento manual.", null, 33, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("40000000-0000-0000-0000-000000000006"), "Empresa", false, null, null, false, true, "ObligacionLegal", "Modalidad Preventiva", "Vigente hasta cambios — vencimiento manual. Obligatorio por defecto (2026-08-09): un único tipo cubre la modalidad preventiva de la empresa, sea SPA, Propia o Mancomunada — no se modela como alternativas separadas (ver ROADMAP.md).", null, 34, "Ninguno", "Si", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("40000000-0000-0000-0000-000000000007"), "Empresa", false, null, null, false, true, "RequisitoCliente", "Escritura de Constitución", "Documento permanente — algunos clientes lo piden, no todos.", null, 35, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("40000000-0000-0000-0000-000000000008"), "Empresa", false, null, null, false, true, "RequisitoCliente", "Poder del Representante Legal", "Vigente hasta modificación — vencimiento manual.", null, 36, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("40000000-0000-0000-0000-000000000009"), "Empresa", false, null, null, false, true, "RequisitoCliente", "ISO 45001", "Certificación opcional — vigencia según auditoría del organismo certificador.", null, 37, "Ninguno", "No", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("4000000a-0000-0000-0000-00000000000a"), "Empresa", false, null, null, false, true, "RequisitoCliente", "ISO 9001", "Certificación opcional — vigencia según auditoría del organismo certificador.", null, 38, "Ninguno", "No", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("4000000b-0000-0000-0000-00000000000b"), "Empresa", false, null, null, false, true, "RequisitoCliente", "ISO 14001", "Certificación opcional — vigencia según auditoría del organismo certificador.", null, 39, "Ninguno", "No", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("4000000c-0000-0000-0000-00000000000c"), "Empresa", false, null, null, false, true, "RequisitoCliente", "Declaración Responsable CAE", "Vigencia según lo que exija cada cliente — vencimiento manual.", null, 40, "Ninguno", "No", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("4000000d-0000-0000-0000-00000000000d"), "Empresa", false, null, null, false, true, "RequisitoCliente", "Relación de Maquinaria", "Listado actualizable de la maquinaria de la empresa.", null, 41, "Ninguno", "No", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("4000000e-0000-0000-0000-00000000000e"), "Empresa", false, null, null, false, true, "RequisitoCliente", "VAT europeo", "Solo aplica a empresas extranjeras de la UE.", null, 42, "Ninguno", "No", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("4000000f-0000-0000-0000-00000000000f"), "Empresa", false, null, null, false, true, "ObligacionCondicionada", "Documento acreditativo de empresa extranjera", "Solo aplica a empresas extranjeras.", null, 43, "Ninguno", "No", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("40000010-0000-0000-0000-000000000010"), "Empresa", false, null, null, false, true, "RequisitoCliente", "Traducción jurada", "Solo si el cliente la solicita explícitamente para documentación de una empresa extranjera.", null, 44, "Ninguno", "No", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("40000011-0000-0000-0000-000000000011"), "Empresa", false, null, null, false, true, "ObligacionCondicionada", "Comunicación de desplazamiento", "Solo aplica cuando hay un desplazamiento temporal de trabajadores desde otro país de la UE.", null, 45, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000000-0000-0000-0000-000000000001"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Contrato de Trabajo", "Vigente mientras dure la relación laboral — sin fecha de caducidad propia.", null, 16, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000000-0000-0000-0000-000000000002"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Alta en Seguridad Social", "Vigente mientras continúe contratado — sin fecha de caducidad propia.", null, 17, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000000-0000-0000-0000-000000000003"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Formación Riesgos Específicos", "Vigente hasta cambio de puesto o de riesgos — vencimiento manual.", null, 18, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000000-0000-0000-0000-000000000004"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Formación EPIs", "Distinto de \"Entrega de EPI\" (antes \"EPIS (firma)\"; la entrega/firma de recepción) — esta es la formación de uso.", null, 19, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000000-0000-0000-0000-000000000005"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Permiso de conducir", "Vigencia según DGT, muy variable — vencimiento manual.", null, 20, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000000-0000-0000-0000-000000000006"), "Trabajador", true, null, null, false, true, "RequisitoCliente", "Riesgo Eléctrico", "Renovación cada 3 años, criterio habitual del sector.", null, 21, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, 36 },
                    { new Guid("50000000-0000-0000-0000-000000000007"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Manipulación Manual de Cargas", "Vigencia según política de cada empresa — vencimiento manual.", null, 22, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000000-0000-0000-0000-000000000008"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Manipulación de Productos Químicos", "Vigencia según la actividad — vencimiento manual.", null, 23, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000000-0000-0000-0000-000000000009"), "Trabajador", true, null, null, false, true, "RequisitoCliente", "ADR", "Renovación cada 5 años (transporte de mercancías peligrosas).", null, 24, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, 60 },
                    { new Guid("5000000a-0000-0000-0000-00000000000a"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Soldadura", "Vigencia según política de cada empresa — vencimiento manual.", null, 25, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("5000000b-0000-0000-0000-00000000000b"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Operador de Puente Grúa", "Vigencia según política de cada empresa — vencimiento manual.", null, 26, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("5000000c-0000-0000-0000-00000000000c"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Operador de Grúa Torre", "Vigencia según normativa aplicable — vencimiento manual.", null, 27, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("5000000d-0000-0000-0000-00000000000d"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Operador de Grúa Móvil", "Vigencia según normativa aplicable — vencimiento manual.", null, 28, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("5000000e-0000-0000-0000-00000000000e"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Operador de Dumper", "Vigencia según política de cada empresa — vencimiento manual.", null, 29, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("5000000f-0000-0000-0000-00000000000f"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Operador de Retroexcavadora", "Vigencia según política de cada empresa — vencimiento manual.", null, 30, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000010-0000-0000-0000-000000000010"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Operador de Minicargadora", "Vigencia según política de cada empresa — vencimiento manual.", null, 31, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000011-0000-0000-0000-000000000011"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Operador de Manipulador Telescópico", "Vigencia según política de cada empresa — vencimiento manual.", null, 32, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000012-0000-0000-0000-000000000012"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Permiso de residencia", "Solo aplica a trabajadores extranjeros de fuera de la UE — vencimiento manual.", null, 33, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000013-0000-0000-0000-000000000013"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Permiso de trabajo", "Solo aplica a trabajadores extranjeros de fuera de la UE — vencimiento manual.", null, 34, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000014-0000-0000-0000-000000000014"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Certificado de Registro de Ciudadano de la UE", "Solo aplica a trabajadores extranjeros de la UE — vencimiento manual.", null, 35, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000015-0000-0000-0000-000000000015"), "Trabajador", false, null, null, false, true, "ObligacionCondicionada", "Certificado A1 de Seguridad Social", "Trabajadores desplazados temporalmente desde otro país de la UE — vigencia ligada a la duración del desplazamiento.", null, 36, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("50000016-0000-0000-0000-000000000016"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Documento de identidad", "Verifica identidad y permiso de trabajo. Vigencia según DGT/Extranjería — vencimiento manual. Obligatorio por defecto (2026-08-09).", null, 37, "Ninguno", "Si", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("60000000-0000-0000-0000-000000000001"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Autorización de uso de equipo de trabajo", "F-27 del catálogo de formatos PRL — arts. 3.4 y 5 RD 1215/1997. Vigente hasta modificación — vencimiento manual.", null, 38, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("60000000-0000-0000-0000-000000000002"), "Empresa", false, null, null, false, true, "RequisitoCliente", "Acta de presencia del recurso preventivo", "F-41 del catálogo de formatos PRL — práctica probatoria del art. 32 bis.3 LPRL. Un acta por presencia — vencimiento manual.", null, 46, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("60000000-0000-0000-0000-000000000003"), "Empresa", false, null, null, false, true, "RequisitoCliente", "Acta de reunión de coordinación", "F-47 del catálogo de formatos PRL — art. 11.b y 11.c RD 171/2004. Un acta por reunión — vencimiento manual.", null, 47, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("60000000-0000-0000-0000-000000000004"), "Empresa", false, null, null, false, true, "RequisitoCliente", "Informe de investigación de accidente o incidente", "F-70 del catálogo de formatos PRL — art. 16.3 LPRL. Un informe por accidente o incidente — vencimiento manual.", null, 48, "Ninguno", "No", null, "CategoriaEspecialSalud", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("60000000-0000-0000-0000-000000000005"), "Empresa", false, null, null, false, true, "RequisitoCliente", "Protocolo frente al acoso sexual y por razón de sexo", "F-93 del catálogo de formatos PRL — art. 48 LO 3/2007. Obligatorio para todas las empresas, sin umbral de plantilla. Vigente hasta revisión — vencimiento manual.", null, 49, "Ninguno", "No", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("60000000-0000-0000-0000-000000000006"), "Empresa", false, null, null, false, true, "RequisitoCliente", "Registro retributivo", "F-95 del catálogo de formatos PRL — RD 902/2020. Obligatorio para todas las empresas, sin umbral de plantilla. Vigente hasta revisión — vencimiento manual.", null, 50, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("70000000-0000-0000-0000-000000000001"), "Empresa", false, null, null, false, true, "RequisitoCliente", "Información de riesgos propios aportados al centro", "F-44 del catálogo de formatos PRL — art. 4.2 RD 171/2004. El formato Outbound por excelencia: lo emite el contratista hacia el titular del centro. Vigente hasta modificación de los riesgos — vencimiento manual.", null, 51, "Ninguno", "No", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("70000000-0000-0000-0000-000000000002"), "Empresa", false, null, null, false, true, "RequisitoCliente", "Información y coordinación con trabajadores autónomos", "F-52 del catálogo de formatos PRL — art. 24.5 LPRL · art. 4.1 RD 171/2004. Vigente hasta modificación — vencimiento manual.", null, 52, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("70000000-0000-0000-0000-000000000003"), "Empresa", false, null, null, false, true, "RequisitoCliente", "Registro del deber de vigilancia sobre subcontratas", "F-50 del catálogo de formatos PRL — art. 24.3 LPRL · art. 10 RD 171/2004. El único de la familia D que la ley impone directamente al contratista principal sobre sus Subcontratas. Un registro por subcontrata/verificación — vencimiento manual.", null, 53, "Ninguno", "No", null, "SinDatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null },
                    { new Guid("70000000-0000-0000-0000-000000000004"), "Trabajador", false, null, null, false, true, "RequisitoCliente", "Recibí de normas, procedimientos y plan de emergencia", "F-29 del catálogo de formatos PRL — arts. 18 y 20 LPRL. Vigente hasta modificación de normas/plan — vencimiento manual.", null, 39, "Ninguno", "No", null, "DatosPersonales", new Guid("00000000-0000-0000-0000-000000000001"), false, null }
                });

            migrationBuilder.InsertData(
                table: "TiposDocumentoAlias",
                columns: new[] { "Id", "TenantId", "Texto", "TipoDocumentoId" },
                values: new object[,]
                {
                    { new Guid("90000000-0000-0000-0000-000000000001"), new Guid("00000000-0000-0000-0000-000000000001"), "Apto médico laboral", new Guid("10000000-0000-0000-0000-000000000001") },
                    { new Guid("90000000-0000-0000-0000-000000000002"), new Guid("00000000-0000-0000-0000-000000000001"), "EPIS (firma)", new Guid("10000000-0000-0000-0000-000000000002") },
                    { new Guid("90000000-0000-0000-0000-000000000003"), new Guid("00000000-0000-0000-0000-000000000001"), "DNI o NIE en vigor", new Guid("50000016-0000-0000-0000-000000000016") },
                    { new Guid("90000000-0000-0000-0000-000000000004"), new Guid("00000000-0000-0000-0000-000000000001"), "DNI/NIE/TIE", new Guid("50000016-0000-0000-0000-000000000016") },
                    { new Guid("90000000-0000-0000-0000-000000000005"), new Guid("00000000-0000-0000-0000-000000000001"), "RLC/TC1", new Guid("20000000-0000-0000-0000-000000000004") },
                    { new Guid("90000000-0000-0000-0000-000000000006"), new Guid("00000000-0000-0000-0000-000000000001"), "TC1", new Guid("20000000-0000-0000-0000-000000000004") },
                    { new Guid("90000000-0000-0000-0000-000000000007"), new Guid("00000000-0000-0000-0000-000000000001"), "RNT/TC2", new Guid("20000000-0000-0000-0000-000000000007") },
                    { new Guid("90000000-0000-0000-0000-000000000008"), new Guid("00000000-0000-0000-0000-000000000001"), "TC2", new Guid("20000000-0000-0000-0000-000000000007") },
                    { new Guid("90000000-0000-0000-0000-000000000009"), new Guid("00000000-0000-0000-0000-000000000001"), "SPA (Servicio de Prevención Ajeno)", new Guid("2000000a-0000-0000-0000-00000000000a") },
                    { new Guid("90000000-0000-0000-0000-000000000010"), new Guid("00000000-0000-0000-0000-000000000001"), "SPA", new Guid("2000000a-0000-0000-0000-00000000000a") },
                    { new Guid("90000000-0000-0000-0000-000000000011"), new Guid("00000000-0000-0000-0000-000000000001"), "EVR (Evaluación de Riesgos Laborales)", new Guid("2000000b-0000-0000-0000-00000000000b") },
                    { new Guid("90000000-0000-0000-0000-000000000012"), new Guid("00000000-0000-0000-0000-000000000001"), "EVR", new Guid("2000000b-0000-0000-0000-00000000000b") },
                    { new Guid("90000000-0000-0000-0000-000000000013"), new Guid("00000000-0000-0000-0000-000000000001"), "PAP (Planificación de la Actividad Preventiva)", new Guid("2000000c-0000-0000-0000-00000000000c") },
                    { new Guid("90000000-0000-0000-0000-000000000014"), new Guid("00000000-0000-0000-0000-000000000001"), "PAP", new Guid("2000000c-0000-0000-0000-00000000000c") },
                    { new Guid("90000000-0000-0000-0000-000000000015"), new Guid("00000000-0000-0000-0000-000000000001"), "Tarjeta CIF", new Guid("2000000d-0000-0000-0000-00000000000d") },
                    { new Guid("90000000-0000-0000-0000-000000000016"), new Guid("00000000-0000-0000-0000-000000000001"), "CIF", new Guid("2000000d-0000-0000-0000-00000000000d") }
                });

            // 3. Particionado mensual de los dos registros de auditoría, con el mismo
            //    helper que ParticionarAuditoriaPorMes: meses relativos a la fecha de
            //    migración y privilegios y políticas copiados a cada partición.
            migrationBuilder.Sql(ParticionadoMensualEventos.CrearFuncionesSql);
            foreach (var (tabla, columna) in ParticionadoMensualEventos.Tablas)
                migrationBuilder.Sql(ParticionadoMensualEventos.ParticionarSql(tabla, columna));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "La línea base compactada no se deshace: crea el esquema entero. Para volver a una imagen "
                + "anterior a la compactación, deploy/volver-atras.sh devuelve el historial previo.");
        }
    }
}
