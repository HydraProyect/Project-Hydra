using CaeManager.Domain.Documentos;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Catálogo maestro real, tomado de la hoja "Parametros" del cuadro de
/// control en Excel que este sistema reemplaza (ver DATABASE.md), más los
/// documentos base de Empresa y de Vehículo indicados directamente por el
/// usuario (2026-07). Los Id son fijos y deterministas para que las
/// migraciones sean reproducibles.
///
/// <c>EsObligatorio</c> del lado Trabajador (2026-08-09, indicado
/// directamente por el usuario): en España, para que una empresa entre a
/// trabajar en una planta industrial mediante CAE, casi ningún centro pide
/// menos que el DNI/NIE en vigor, el apto médico, la formación PRL
/// (Art. 19), el justificante de entrega de EPIs y el registro de
/// información de riesgos del puesto (Art. 18) de cada trabajador — el
/// lado Empresa (EVR/PAP, corriente con SS/Hacienda, RLC/RNT, seguro de
/// RC, concierto con el SPA) ya estaba marcado obligatorio desde antes.
/// Un centro que exija menos lo desmarca desde /tipos-documento →
/// "Requisitos del Centro" (<c>TipoDocumentoCentro</c> manda sobre este
/// criterio global, ver <c>ResolucionTipoDocumentoCentro</c>): no hace
/// falta ninguna decisión de UI nueva, el mecanismo ya existía.
///
/// "Modalidad Preventiva" (Empresa) pasa a obligatoria por defecto el
/// mismo día (2026-08-09, indicado directamente por el usuario): un único
/// tipo de documento cubre la modalidad preventiva de la empresa, sea SPA,
/// Propia o Mancomunada — no se modela como grupo de alternativas.
/// </summary>
public static class TipoDocumentoSeedData
{
    public static readonly (Guid Id, string Nombre, int? VigenciaMeses, bool AplicaVencimiento, int Orden, AmbitoAplicacion Ambito, bool EsObligatorio, string? Notas)[] Datos =
    [
        // --- Catálogo original de Trabajador (Fase 0, hoja "Parametros" del Excel) ---
        (new Guid("10000000-0000-0000-0000-000000000001"), "Apto médico laboral", 12, true, 1, AmbitoAplicacion.Trabajador, true, "Renovación anual estándar. Obligatorio por defecto (2026-08-09): sí o sí exigido en CAE."),
        (new Guid("10000000-0000-0000-0000-000000000002"), "EPIS (firma)", 12, true, 2, AmbitoAplicacion.Trabajador, true, "Se firman cada año según nota de origen. Obligatorio por defecto (2026-08-09): justificante de entrega de EPIs."),
        (new Guid("10000000-0000-0000-0000-000000000003"), "Reciclaje 4h", 48, true, 3, AmbitoAplicacion.Trabajador, false, "Cada 4 años, según Dpto. Formación."),
        (new Guid("10000000-0000-0000-0000-000000000004"), "Formación Art. 19", 36, true, 4, AmbitoAplicacion.Trabajador, true, "Recordatorio cada 3 años. Obligatorio por defecto (2026-08-09): formación PRL Art. 19."),
        (new Guid("10000000-0000-0000-0000-000000000005"), "Formación 60h (base convenio)", null, false, 5, AmbitoAplicacion.Trabajador, false, "Formación base, no consta caducidad."),
        (new Guid("10000000-0000-0000-0000-000000000006"), "Formación 20h", null, false, 6, AmbitoAplicacion.Trabajador, false, "Mismo curso de convenio que 60h/6h, no consta caducidad."),
        (new Guid("10000000-0000-0000-0000-000000000007"), "Formación 6h", null, false, 7, AmbitoAplicacion.Trabajador, false, "Mismo curso de convenio que 60h/20h, no consta caducidad."),
        (new Guid("10000000-0000-0000-0000-000000000008"), "Información Art. 18", null, false, 8, AmbitoAplicacion.Trabajador, true, "No consta periodicidad de renovación. Obligatorio por defecto (2026-08-09): registro de entrega de información de riesgos del puesto."),
        (new Guid("10000000-0000-0000-0000-000000000009"), "Carretillas elevadoras", null, false, 9, AmbitoAplicacion.Trabajador, false, "Configurable si el convenio interno define vigencia."),
        (new Guid("10000000-0000-0000-0000-00000000000A"), "PEMP (plataformas elevadoras)", null, false, 10, AmbitoAplicacion.Trabajador, false, "Configurable si el convenio interno define vigencia."),
        (new Guid("10000000-0000-0000-0000-00000000000B"), "LOTO (4h)", null, false, 11, AmbitoAplicacion.Trabajador, false, "Configurable si el convenio interno define vigencia."),
        (new Guid("10000000-0000-0000-0000-00000000000C"), "Seguridad alimentaria", null, false, 12, AmbitoAplicacion.Trabajador, false, "Configurable si el convenio interno define vigencia."),
        (new Guid("10000000-0000-0000-0000-00000000000D"), "Primeros auxilios", null, false, 13, AmbitoAplicacion.Trabajador, false, "Se recomienda revisar cada 2 años; sin dato oficial de origen."),
        (new Guid("10000000-0000-0000-0000-00000000000E"), "Espacios confinados", null, false, 14, AmbitoAplicacion.Trabajador, false, "Configurable si el convenio interno define vigencia."),
        (new Guid("10000000-0000-0000-0000-00000000000F"), "Trabajos en altura (8h)", null, false, 15, AmbitoAplicacion.Trabajador, false, "Configurable si el convenio interno define vigencia."),

        // --- Ampliación del catálogo de Trabajador (Fase 37, 2026-07, a partir de un listado de documentación CAE/PRL de España pasado por el usuario) ---
        (new Guid("50000000-0000-0000-0000-000000000001"), "Contrato de Trabajo", null, false, 16, AmbitoAplicacion.Trabajador, false, "Vigente mientras dure la relación laboral — sin fecha de caducidad propia."),
        (new Guid("50000000-0000-0000-0000-000000000002"), "Alta en Seguridad Social", null, false, 17, AmbitoAplicacion.Trabajador, false, "Vigente mientras continúe contratado — sin fecha de caducidad propia."),
        (new Guid("50000000-0000-0000-0000-000000000003"), "Formación Riesgos Específicos", null, false, 18, AmbitoAplicacion.Trabajador, false, "Vigente hasta cambio de puesto o de riesgos — vencimiento manual."),
        (new Guid("50000000-0000-0000-0000-000000000004"), "Formación EPIs", null, false, 19, AmbitoAplicacion.Trabajador, false, "Distinto de \"EPIS (firma)\" (la entrega/firma de recepción) — esta es la formación de uso."),
        (new Guid("50000000-0000-0000-0000-000000000005"), "Permiso de conducir", null, false, 20, AmbitoAplicacion.Trabajador, false, "Vigencia según DGT, muy variable — vencimiento manual."),
        (new Guid("50000000-0000-0000-0000-000000000006"), "Riesgo Eléctrico", 36, true, 21, AmbitoAplicacion.Trabajador, false, "Renovación cada 3 años, criterio habitual del sector."),
        (new Guid("50000000-0000-0000-0000-000000000007"), "Manipulación Manual de Cargas", null, false, 22, AmbitoAplicacion.Trabajador, false, "Vigencia según política de cada empresa — vencimiento manual."),
        (new Guid("50000000-0000-0000-0000-000000000008"), "Manipulación de Productos Químicos", null, false, 23, AmbitoAplicacion.Trabajador, false, "Vigencia según la actividad — vencimiento manual."),
        (new Guid("50000000-0000-0000-0000-000000000009"), "ADR", 60, true, 24, AmbitoAplicacion.Trabajador, false, "Renovación cada 5 años (transporte de mercancías peligrosas)."),
        (new Guid("5000000A-0000-0000-0000-00000000000A"), "Soldadura", null, false, 25, AmbitoAplicacion.Trabajador, false, "Vigencia según política de cada empresa — vencimiento manual."),
        (new Guid("5000000B-0000-0000-0000-00000000000B"), "Operador de Puente Grúa", null, false, 26, AmbitoAplicacion.Trabajador, false, "Vigencia según política de cada empresa — vencimiento manual."),
        (new Guid("5000000C-0000-0000-0000-00000000000C"), "Operador de Grúa Torre", null, false, 27, AmbitoAplicacion.Trabajador, false, "Vigencia según normativa aplicable — vencimiento manual."),
        (new Guid("5000000D-0000-0000-0000-00000000000D"), "Operador de Grúa Móvil", null, false, 28, AmbitoAplicacion.Trabajador, false, "Vigencia según normativa aplicable — vencimiento manual."),
        (new Guid("5000000E-0000-0000-0000-00000000000E"), "Operador de Dumper", null, false, 29, AmbitoAplicacion.Trabajador, false, "Vigencia según política de cada empresa — vencimiento manual."),
        (new Guid("5000000F-0000-0000-0000-00000000000F"), "Operador de Retroexcavadora", null, false, 30, AmbitoAplicacion.Trabajador, false, "Vigencia según política de cada empresa — vencimiento manual."),
        (new Guid("50000010-0000-0000-0000-000000000010"), "Operador de Minicargadora", null, false, 31, AmbitoAplicacion.Trabajador, false, "Vigencia según política de cada empresa — vencimiento manual."),
        (new Guid("50000011-0000-0000-0000-000000000011"), "Operador de Manipulador Telescópico", null, false, 32, AmbitoAplicacion.Trabajador, false, "Vigencia según política de cada empresa — vencimiento manual."),
        (new Guid("50000012-0000-0000-0000-000000000012"), "Permiso de residencia", null, false, 33, AmbitoAplicacion.Trabajador, false, "Solo aplica a trabajadores extranjeros de fuera de la UE — vencimiento manual."),
        (new Guid("50000013-0000-0000-0000-000000000013"), "Permiso de trabajo", null, false, 34, AmbitoAplicacion.Trabajador, false, "Solo aplica a trabajadores extranjeros de fuera de la UE — vencimiento manual."),
        (new Guid("50000014-0000-0000-0000-000000000014"), "Certificado de Registro de Ciudadano de la UE", null, false, 35, AmbitoAplicacion.Trabajador, false, "Solo aplica a trabajadores extranjeros de la UE — vencimiento manual."),
        (new Guid("50000015-0000-0000-0000-000000000015"), "Certificado A1 de Seguridad Social", null, false, 36, AmbitoAplicacion.Trabajador, false, "Trabajadores desplazados temporalmente desde otro país de la UE — vigencia ligada a la duración del desplazamiento."),

        // --- Documento base de identidad del Trabajador (2026-08-09, indicado
        // directamente por el usuario: sí o sí exigido en cualquier CAE) ---
        (new Guid("50000016-0000-0000-0000-000000000016"), "DNI o NIE en vigor", null, false, 37, AmbitoAplicacion.Trabajador, true, "Verifica identidad y permiso de trabajo. Vigencia según DGT/Extranjería — vencimiento manual. Obligatorio por defecto (2026-08-09)."),

        // --- Documentos base de Empresa, obligatorios para todos los clientes (2026-07, indicados directamente por el usuario) ---
        (new Guid("20000000-0000-0000-0000-000000000001"), "Certificado de estar al corriente con la Seguridad Social", 1, true, 16, AmbitoAplicacion.Empresa, true, "Mensual."),
        (new Guid("20000000-0000-0000-0000-000000000002"), "Certificado de estar al corriente con Hacienda", null, false, 17, AmbitoAplicacion.Empresa, true, "Vigencia variable (1, 3, 6 o 12 meses según lo que exija el cliente) — la fecha de vencimiento se introduce a mano al subir el documento."),
        (new Guid("20000000-0000-0000-0000-000000000003"), "ITA", 1, true, 18, AmbitoAplicacion.Empresa, true, "Mensual."),
        (new Guid("20000000-0000-0000-0000-000000000004"), "RLC/TC1", 3, true, 19, AmbitoAplicacion.Empresa, true, "Mensual — el documento de un periodo (p. ej. 01/05) vence 3 meses después (01/08), porque tarda en emitirse con la fecha del periodo ya pasada."),
        (new Guid("20000000-0000-0000-0000-000000000005"), "Recibo de pago RLC/TC1", 3, true, 20, AmbitoAplicacion.Empresa, true, "Mismo criterio de vigencia que el RLC/TC1."),
        (new Guid("20000000-0000-0000-0000-000000000006"), "RLC/TC1 + Recibo de pago", 3, true, 21, AmbitoAplicacion.Empresa, true, "Variante combinada — mismo criterio de vigencia que el RLC/TC1."),
        (new Guid("20000000-0000-0000-0000-000000000007"), "RNT/TC2", 3, true, 22, AmbitoAplicacion.Empresa, true, "Mismo criterio que el RLC/TC1."),
        (new Guid("20000000-0000-0000-0000-000000000008"), "Mutua", null, false, 23, AmbitoAplicacion.Empresa, true, "Vigencia sin especificar — fecha de vencimiento manual."),
        (new Guid("20000000-0000-0000-0000-000000000009"), "Seguro de Responsabilidad Civil + recibo de pago", null, false, 24, AmbitoAplicacion.Empresa, true, "Vigencia sin especificar — fecha de vencimiento manual."),
        (new Guid("2000000A-0000-0000-0000-00000000000A"), "SPA (Servicio de Prevención Ajeno)", null, false, 25, AmbitoAplicacion.Empresa, true, "Debe venir acompañado de un certificado de pago que indica la fecha fin de validez — se introduce esa fecha manualmente."),
        (new Guid("2000000B-0000-0000-0000-00000000000B"), "EVR (Evaluación de Riesgos Laborales)", null, false, 26, AmbitoAplicacion.Empresa, true, "Vigencia sin especificar — fecha de vencimiento manual."),
        (new Guid("2000000C-0000-0000-0000-00000000000C"), "PAP (Planificación de la Actividad Preventiva)", null, false, 27, AmbitoAplicacion.Empresa, true, "Vigencia sin especificar — fecha de vencimiento manual."),
        (new Guid("2000000D-0000-0000-0000-00000000000D"), "Tarjeta CIF", null, false, 28, AmbitoAplicacion.Empresa, false, "Opcional — no obligatorio para todos los clientes."),

        // --- Ampliación del catálogo de Empresa (Fase 37, 2026-07, a partir de un listado de documentación CAE/PRL de España pasado por el usuario) ---
        (new Guid("40000000-0000-0000-0000-000000000001"), "Plan de Prevención", null, false, 29, AmbitoAplicacion.Empresa, true, "Vigente con revisiones — vencimiento manual."),
        (new Guid("40000000-0000-0000-0000-000000000002"), "Designación de Recursos Preventivos", null, false, 30, AmbitoAplicacion.Empresa, true, "Vigente hasta modificación — vencimiento manual."),
        (new Guid("40000000-0000-0000-0000-000000000003"), "Procedimiento de Coordinación de Actividades Empresariales", null, false, 31, AmbitoAplicacion.Empresa, true, "Vigente hasta revisión — vencimiento manual."),
        (new Guid("40000000-0000-0000-0000-000000000004"), "Política Preventiva", null, false, 32, AmbitoAplicacion.Empresa, false, "Vigente hasta revisión — vencimiento manual."),
        (new Guid("40000000-0000-0000-0000-000000000005"), "Organigrama Preventivo", null, false, 33, AmbitoAplicacion.Empresa, false, "Vigente hasta cambios — vencimiento manual."),
        (new Guid("40000000-0000-0000-0000-000000000006"), "Modalidad Preventiva", null, false, 34, AmbitoAplicacion.Empresa, true, "Vigente hasta cambios — vencimiento manual. Obligatorio por defecto (2026-08-09): un único tipo cubre la modalidad preventiva de la empresa, sea SPA, Propia o Mancomunada — no se modela como alternativas separadas (ver ROADMAP.md)."),
        (new Guid("40000000-0000-0000-0000-000000000007"), "Escritura de Constitución", null, false, 35, AmbitoAplicacion.Empresa, false, "Documento permanente — algunos clientes lo piden, no todos."),
        (new Guid("40000000-0000-0000-0000-000000000008"), "Poder del Representante Legal", null, false, 36, AmbitoAplicacion.Empresa, false, "Vigente hasta modificación — vencimiento manual."),
        (new Guid("40000000-0000-0000-0000-000000000009"), "ISO 45001", null, false, 37, AmbitoAplicacion.Empresa, false, "Certificación opcional — vigencia según auditoría del organismo certificador."),
        (new Guid("4000000A-0000-0000-0000-00000000000A"), "ISO 9001", null, false, 38, AmbitoAplicacion.Empresa, false, "Certificación opcional — vigencia según auditoría del organismo certificador."),
        (new Guid("4000000B-0000-0000-0000-00000000000B"), "ISO 14001", null, false, 39, AmbitoAplicacion.Empresa, false, "Certificación opcional — vigencia según auditoría del organismo certificador."),
        (new Guid("4000000C-0000-0000-0000-00000000000C"), "Declaración Responsable CAE", null, false, 40, AmbitoAplicacion.Empresa, false, "Vigencia según lo que exija cada cliente — vencimiento manual."),
        (new Guid("4000000D-0000-0000-0000-00000000000D"), "Relación de Maquinaria", null, false, 41, AmbitoAplicacion.Empresa, false, "Listado actualizable de la maquinaria de la empresa."),
        (new Guid("4000000E-0000-0000-0000-00000000000E"), "VAT europeo", null, false, 42, AmbitoAplicacion.Empresa, false, "Solo aplica a empresas extranjeras de la UE."),
        (new Guid("4000000F-0000-0000-0000-00000000000F"), "Documento acreditativo de empresa extranjera", null, false, 43, AmbitoAplicacion.Empresa, false, "Solo aplica a empresas extranjeras."),
        (new Guid("40000010-0000-0000-0000-000000000010"), "Traducción jurada", null, false, 44, AmbitoAplicacion.Empresa, false, "Solo si el cliente la solicita explícitamente para documentación de una empresa extranjera."),
        (new Guid("40000011-0000-0000-0000-000000000011"), "Comunicación de desplazamiento", null, false, 45, AmbitoAplicacion.Empresa, false, "Solo aplica cuando hay un desplazamiento temporal de trabajadores desde otro país de la UE."),

        // --- Documentos de Vehículo (2026-07, indicados directamente por el
        // usuario; vencimiento anual desde 2026-08-10 — decisión del
        // propietario: toda la documentación de vehículo vence) ---
        (new Guid("30000000-0000-0000-0000-000000000001"), "ITC", 12, true, 1, AmbitoAplicacion.Vehiculo, true, "Vigencia anual."),
        (new Guid("30000000-0000-0000-0000-000000000002"), "Ficha técnica", 12, true, 2, AmbitoAplicacion.Vehiculo, true, "Vigencia anual."),
        (new Guid("30000000-0000-0000-0000-000000000003"), "Seguro", 12, true, 3, AmbitoAplicacion.Vehiculo, true, "Vigencia anual."),
        (new Guid("30000000-0000-0000-0000-000000000004"), "Autorización de circulación", 12, true, 4, AmbitoAplicacion.Vehiculo, true, "Vigencia anual."),

        // --- Tramo 0.3 del MVP-1 de formatos (2026-08-14): altas de TipoDocumento
        // que faltaban para poder archivar seis de los formatos del censo de
        // CATALOGO_FORMATOS_PRL.md. F-107 ("Comunicación de desplazamiento a la
        // autoridad laboral") NO se da de alta aquí: ya existe como "Comunicación
        // de desplazamiento" (Id 40000011-...-000000000011, línea de arriba) —
        // verificado antes de añadir para no duplicar. Ninguno se marca
        // EsObligatorio=true por defecto pese a que F-93/F-95 no tienen umbral de
        // plantilla legal (CATALOGO_FORMATOS_PRL.md § 5.5): activarlo por defecto
        // cambiaría de golpe el estado de cumplimiento de todos los centros
        // existentes sin que el propietario lo haya confirmado — queda a criterio
        // de cada Administrador vía /tipos-documento.
        (new Guid("60000000-0000-0000-0000-000000000001"), "Autorización de uso de equipo de trabajo", null, false, 38, AmbitoAplicacion.Trabajador, false, "F-27 del catálogo de formatos PRL — arts. 3.4 y 5 RD 1215/1997. Vigente hasta modificación — vencimiento manual."),
        (new Guid("60000000-0000-0000-0000-000000000002"), "Acta de presencia del recurso preventivo", null, false, 46, AmbitoAplicacion.Empresa, false, "F-41 del catálogo de formatos PRL — práctica probatoria del art. 32 bis.3 LPRL. Un acta por presencia — vencimiento manual."),
        (new Guid("60000000-0000-0000-0000-000000000003"), "Acta de reunión de coordinación", null, false, 47, AmbitoAplicacion.Empresa, false, "F-47 del catálogo de formatos PRL — art. 11.b y 11.c RD 171/2004. Un acta por reunión — vencimiento manual."),
        (new Guid("60000000-0000-0000-0000-000000000004"), "Informe de investigación de accidente o incidente", null, false, 48, AmbitoAplicacion.Empresa, false, "F-70 del catálogo de formatos PRL — art. 16.3 LPRL. Un informe por accidente o incidente — vencimiento manual."),
        (new Guid("60000000-0000-0000-0000-000000000005"), "Protocolo frente al acoso sexual y por razón de sexo", null, false, 49, AmbitoAplicacion.Empresa, false, "F-93 del catálogo de formatos PRL — art. 48 LO 3/2007. Obligatorio para todas las empresas, sin umbral de plantilla. Vigente hasta revisión — vencimiento manual."),
        (new Guid("60000000-0000-0000-0000-000000000006"), "Registro retributivo", null, false, 50, AmbitoAplicacion.Empresa, false, "F-95 del catálogo de formatos PRL — RD 902/2020. Obligatorio para todas las empresas, sin umbral de plantilla. Vigente hasta revisión — vencimiento manual."),

        // --- Tramo 1.2 del MVP-1 de formatos (2026-08-14): al construir la
        // plantilla impresa de F-44/F-52/F-50/F-29, se detectó que tampoco
        // tenían TipoDocumento (CATALOGO_FORMATOS_PRL.md § 9.2 clasifica solo
        // F-53 como "tipo existente" para este tramo — F-44/F-52/F-50/F-29
        // habían quedado fuera de esa clasificación por error, verificado en
        // TipoDocumentoSeedData.cs antes de asumirlo). F-53 ("Declaración
        // Responsable CAE") no se repite aquí: ya existe (Id
        // 4000000C-...-00000000000C, línea de arriba).
        (new Guid("70000000-0000-0000-0000-000000000001"), "Información de riesgos propios aportados al centro", null, false, 51, AmbitoAplicacion.Empresa, false, "F-44 del catálogo de formatos PRL — art. 4.2 RD 171/2004. El formato Outbound por excelencia: lo emite el contratista hacia el titular del centro. Vigente hasta modificación de los riesgos — vencimiento manual."),
        (new Guid("70000000-0000-0000-0000-000000000002"), "Información y coordinación con trabajadores autónomos", null, false, 52, AmbitoAplicacion.Empresa, false, "F-52 del catálogo de formatos PRL — art. 24.5 LPRL · art. 4.1 RD 171/2004. Vigente hasta modificación — vencimiento manual."),
        (new Guid("70000000-0000-0000-0000-000000000003"), "Registro del deber de vigilancia sobre subcontratas", null, false, 53, AmbitoAplicacion.Empresa, false, "F-50 del catálogo de formatos PRL — art. 24.3 LPRL · art. 10 RD 171/2004. El único de la familia D que la ley impone directamente al contratista principal sobre sus Subcontratas. Un registro por subcontrata/verificación — vencimiento manual."),
        (new Guid("70000000-0000-0000-0000-000000000004"), "Recibí de normas, procedimientos y plan de emergencia", null, false, 39, AmbitoAplicacion.Trabajador, false, "F-29 del catálogo de formatos PRL — arts. 18 y 20 LPRL. Vigente hasta modificación de normas/plan — vencimiento manual."),
    ];

    /// <summary>
    /// Tipos de documento de Empresa que traen un listado de personal y por
    /// tanto tiene sentido activarles la detección automática de altas/bajas
    /// de trabajadores (Fase 36) — el resto (certificados, seguros, mutua...)
    /// no contienen ningún listado, así que activarla generaría llamadas a
    /// IA y "detecciones" sin sentido.
    /// </summary>
    private static readonly HashSet<Guid> IdsConDeteccionTrabajadores =
    [
        new Guid("20000000-0000-0000-0000-000000000003"), // ITA
        new Guid("20000000-0000-0000-0000-000000000007"), // RNT/TC2
    ];

    /// <summary>
    /// Tipos de Empresa que son documentos oficiales de la Administración
    /// con validación automática (verificación de firma + parser, ver
    /// PerfilDocumentoOficial). El "Recibo de pago RLC/TC1" suelto queda
    /// fuera: es un justificante bancario, no un documento sellado por la
    /// TGSS. La variante combinada comparte parser con el RLC — si la
    /// calibración con muestras reales pide un ancla extra del recibo, se
    /// ajusta entonces (plan, PR-6).
    /// </summary>
    private static readonly Dictionary<Guid, PerfilDocumentoOficial> PerfilesOficiales = new()
    {
        [new Guid("20000000-0000-0000-0000-000000000001")] = PerfilDocumentoOficial.CorrienteTgss,
        [new Guid("20000000-0000-0000-0000-000000000002")] = PerfilDocumentoOficial.CorrienteAeat,
        [new Guid("20000000-0000-0000-0000-000000000003")] = PerfilDocumentoOficial.Ita,
        [new Guid("20000000-0000-0000-0000-000000000004")] = PerfilDocumentoOficial.Rlc,
        [new Guid("20000000-0000-0000-0000-000000000006")] = PerfilDocumentoOficial.Rlc,
        [new Guid("20000000-0000-0000-0000-000000000007")] = PerfilDocumentoOficial.Rnt,
    };

    /// <summary>
    /// Flags de IA por nombre, para las copias por tenant (ver
    /// DelegacionDemoSeeder): el constructor de TipoDocumento no los expone
    /// y el HasData que sí los fija solo cubre el tenant #1 — sin esto, las
    /// copias de los tenants de demo nacen sin detección de trabajadores ni
    /// perfil oficial y esos flujos no se pueden ejercitar allí.
    /// </summary>
    public static bool TieneDeteccionTrabajadores(string nombre) =>
        Datos.Any(d => d.Nombre == nombre && IdsConDeteccionTrabajadores.Contains(d.Id));

    public static PerfilDocumentoOficial PerfilOficialDe(string nombre) =>
        Datos.Where(d => d.Nombre == nombre)
            .Select(d => PerfilesOficiales.GetValueOrDefault(d.Id, PerfilDocumentoOficial.Ninguno))
            .FirstOrDefault();

    /// <summary>
    /// Copia editable del catálogo completo para un tenant nuevo
    /// (docs/MULTITENANCY.md § 7) con los flags de IA aplicados — la usan
    /// DelegacionDemoSeeder y SegundoTenantSeeder para no repetir la
    /// construcción (y para que ningún aprovisionamiento vuelva a olvidarse
    /// de los flags, como pasó con los tenants de demo).
    /// </summary>
    public static IEnumerable<TipoDocumento> CrearCopiasParaTenant() =>
        Datos.Select(t =>
        {
            var copia = new TipoDocumento(
                t.Nombre, t.VigenciaMeses, t.AplicaVencimiento, t.Orden, t.Ambito, t.EsObligatorio, t.Notas);
            copia.EstablecerDeteccionTrabajadoresActiva(TieneDeteccionTrabajadores(t.Nombre));
            copia.EstablecerPerfilDocumentoOficial(PerfilOficialDe(t.Nombre));
            return copia;
        });

    /// <summary>Proyección plana usada por HasData (necesita anonymous/objeto con las propiedades de la entidad).</summary>
    public static IEnumerable<object> ComoFilasParaMigracion() =>
        Datos.Select(d => new
        {
            Id = d.Id,
            Nombre = d.Nombre,
            VigenciaMeses = d.VigenciaMeses,
            AplicaVencimientoAutomatico = d.AplicaVencimiento,
            Orden = d.Orden,
            Notas = d.Notas,
            AmbitoAplicacion = d.Ambito,
            EsObligatorio = d.EsObligatorio,
            LecturaIaActiva = true,
            DeteccionTrabajadoresActiva = IdsConDeteccionTrabajadores.Contains(d.Id),
            // Empieza desactivada para todo el catálogo semilla — mismo
            // criterio que DeteccionTrabajadoresActiva: el Administrador la
            // activa explícitamente solo donde interesa (ver Issue #19).
            VerificacionIaActiva = false,
            PerfilDocumentoOficial = PerfilesOficiales.GetValueOrDefault(d.Id, PerfilDocumentoOficial.Ninguno),
            // El catálogo semilla pertenece al tenant #1 (ver Etapa 2 de
            // PLAN-MIGRACION-MULTITENANT.md) — un tenant nuevo recibirá su
            // propia copia editable al aprovisionarse (docs/MULTITENANCY.md § 7),
            // no una referencia a esta misma fila.
            TenantId = TenantSeedData.IdPorDefecto
        });
}
