using CaeManager.Domain.Documentos;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Catálogo maestro real, tomado de la hoja "Parametros" del cuadro de
/// control en Excel que este sistema reemplaza (ver Project-Hydra-Negocio/tecnico/DATABASE.md), más los
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
///
/// <c>Nombre</c> tras T3 (2026-08-29, Project-Hydra-Negocio/tecnico/taxonomia-documental-cae-propuesta-2026-08-27.md
/// §2bis, aprobada 2026-08-27 tarde): limpieza de los patrones A/B/C/D/E de
/// nombres contaminados — estado, resultado, formato de evidencia, alias
/// histórico y acrónimo dentro del nombre. Cada tipo tocado conserva el
/// nombre contaminado anterior como <see cref="TipoDocumentoAlias"/> (ver
/// <see cref="AliasesPorId"/>) para que nada que busque por el nombre viejo
/// deje de encontrarlo.
///
/// Revisión del catálogo aprobada el 2026-10-17 (revisión del catálogo de tipos de documento del piloto Outbound,
/// en Project-Hydra-Negocio): cada tipo lleva grupo (<see cref="GrupoPorId"/>); los
/// patrones G (horas en el nombre) y H (artículo en el nombre) se resuelven con nombres funcionales, y el nombre
/// anterior queda como alias; las horas y el nivel pasan a ser datos del documento
/// (<see cref="DatosAdicionalesPorId"/>). Las formaciones de convenio de 60 h, 20 h y 6 h siguen siendo tres
/// tipos: son documentos distintos (nivel básico, oficio y módulo específico del VII CGSC) y uno no vale por otro.
/// El patrón F (dos documentos en un tipo) se resuelve con un solo tipo, «RLC (TC1)» o «Seguro de Responsabilidad
/// Civil», con el recibo como adjunto opcional; los tipos sobrantes quedan archivados con sustituto
/// (<see cref="ArchivadosConSustituto"/>), sin tocar ningún Documento.
/// </summary>
public static class TipoDocumentoSeedData
{
    public static readonly (Guid Id, string Nombre, int? VigenciaMeses, bool AplicaVencimiento, int Orden, AmbitoAplicacion Ambito, bool EsObligatorio, string? Notas)[] Datos =
    [
        // --- Catálogo original de Trabajador (Fase 0, hoja "Parametros" del Excel) ---
        (new Guid("10000000-0000-0000-0000-000000000001"), "Certificado de aptitud médica", 12, true, 1, AmbitoAplicacion.Trabajador, true, "Renovación anual estándar. Obligatorio por defecto (2026-08-09): sí o sí exigido en CAE."),
        (new Guid("10000000-0000-0000-0000-000000000002"), "Entrega de EPI", 12, true, 2, AmbitoAplicacion.Trabajador, true, "Se firman cada año según nota de origen. Obligatorio por defecto (2026-08-09): justificante de entrega de EPIs."),
        (new Guid("10000000-0000-0000-0000-000000000003"), "Reciclaje PRL", 48, true, 3, AmbitoAplicacion.Trabajador, false, "Cada 4 años, según Dpto. Formación."),
        (new Guid("10000000-0000-0000-0000-000000000004"), "Formación PRL del puesto", 36, true, 4, AmbitoAplicacion.Trabajador, true, "Recordatorio cada 3 años. Obligatorio por defecto (2026-08-09): formación PRL Art. 19."),
        (new Guid("10000000-0000-0000-0000-000000000005"), "Formación PRL de convenio", null, false, 5, AmbitoAplicacion.Trabajador, false, "Formación base de convenio (nivel básico de prevención, 60 h en construcción), no consta caducidad. Las horas y el nivel son datos del documento desde 2026-10-17."),
        (new Guid("10000000-0000-0000-0000-000000000006"), "Formación PRL de convenio por oficio", null, false, 6, AmbitoAplicacion.Trabajador, false, "Formación por puesto de trabajo u oficio del convenio (20 h en construcción, art. 141 del VII CGSC). Documento distinto de la formación de nivel básico: no la sustituye. No consta caducidad."),
        (new Guid("10000000-0000-0000-0000-000000000007"), "Formación PRL de convenio por oficio, módulo específico", null, false, 7, AmbitoAplicacion.Trabajador, false, "Módulo o curso específico de oficio del convenio (6 h en construcción, art. 143 del VII CGSC). Documento distinto de la formación de nivel básico y de la de oficio completa. No consta caducidad."),
        (new Guid("10000000-0000-0000-0000-000000000008"), "Información de riesgos del puesto", null, false, 8, AmbitoAplicacion.Trabajador, true, "No consta periodicidad de renovación. Obligatorio por defecto (2026-08-09): registro de entrega de información de riesgos del puesto."),
        (new Guid("10000000-0000-0000-0000-000000000009"), "Carretillas elevadoras", null, false, 9, AmbitoAplicacion.Trabajador, false, "Configurable si el convenio interno define vigencia."),
        (new Guid("10000000-0000-0000-0000-00000000000A"), "PEMP (plataformas elevadoras)", null, false, 10, AmbitoAplicacion.Trabajador, false, "Configurable si el convenio interno define vigencia."),
        (new Guid("10000000-0000-0000-0000-00000000000B"), "Bloqueo y etiquetado (LOTO)", null, false, 11, AmbitoAplicacion.Trabajador, false, "Configurable si el convenio interno define vigencia."),
        (new Guid("10000000-0000-0000-0000-00000000000C"), "Seguridad alimentaria", null, false, 12, AmbitoAplicacion.Trabajador, false, "Configurable si el convenio interno define vigencia."),
        (new Guid("10000000-0000-0000-0000-00000000000D"), "Primeros auxilios", null, false, 13, AmbitoAplicacion.Trabajador, false, "Se recomienda revisar cada 2 años; sin dato oficial de origen."),
        (new Guid("10000000-0000-0000-0000-00000000000E"), "Espacios confinados", null, false, 14, AmbitoAplicacion.Trabajador, false, "Configurable si el convenio interno define vigencia."),
        (new Guid("10000000-0000-0000-0000-00000000000F"), "Trabajos en altura", null, false, 15, AmbitoAplicacion.Trabajador, false, "Configurable si el convenio interno define vigencia."),

        // --- Ampliación del catálogo de Trabajador (Fase 37, 2026-07, a partir de un listado de documentación CAE/PRL de España pasado por el usuario) ---
        (new Guid("50000000-0000-0000-0000-000000000001"), "Contrato de Trabajo", null, false, 16, AmbitoAplicacion.Trabajador, false, "Vigente mientras dure la relación laboral — sin fecha de caducidad propia."),
        (new Guid("50000000-0000-0000-0000-000000000002"), "Alta en Seguridad Social", null, false, 17, AmbitoAplicacion.Trabajador, false, "Vigente mientras continúe contratado — sin fecha de caducidad propia."),
        (new Guid("50000000-0000-0000-0000-000000000003"), "Formación Riesgos Específicos", null, false, 18, AmbitoAplicacion.Trabajador, false, "Vigente hasta cambio de puesto o de riesgos — vencimiento manual."),
        (new Guid("50000000-0000-0000-0000-000000000004"), "Formación EPIs", null, false, 19, AmbitoAplicacion.Trabajador, false, "Archivado el 2026-10-17 (retirado del catálogo base): lo sustituye «Entrega de EPI»."),
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
        (new Guid("50000015-0000-0000-0000-000000000015"), "Certificado A1", null, false, 36, AmbitoAplicacion.Trabajador, false, "Trabajadores desplazados temporalmente desde otro país de la UE — vigencia ligada a la duración del desplazamiento."),

        // --- Documento base de identidad del Trabajador (2026-08-09, indicado
        // directamente por el usuario: sí o sí exigido en cualquier CAE) ---
        (new Guid("50000016-0000-0000-0000-000000000016"), "Documento de identidad", null, false, 37, AmbitoAplicacion.Trabajador, true, "Verifica identidad y permiso de trabajo. Vigencia según DGT/Extranjería — vencimiento manual. Obligatorio por defecto (2026-08-09)."),

        // --- Documentos base de Empresa, obligatorios para todos los clientes (2026-07, indicados directamente por el usuario) ---
        (new Guid("20000000-0000-0000-0000-000000000001"), "Certificado de estar al corriente con la Seguridad Social", 1, true, 16, AmbitoAplicacion.Empresa, true, "Mensual."),
        (new Guid("20000000-0000-0000-0000-000000000002"), "Certificado de estar al corriente con Hacienda", null, false, 17, AmbitoAplicacion.Empresa, true, "Vigencia variable (1, 3, 6 o 12 meses según lo que exija el cliente) — la fecha de vencimiento se introduce a mano al subir el documento."),
        (new Guid("20000000-0000-0000-0000-000000000003"), "Informe de trabajadores en alta", 1, true, 18, AmbitoAplicacion.Empresa, true, "Mensual."),
        (new Guid("20000000-0000-0000-0000-000000000004"), "RLC (TC1)", 3, true, 19, AmbitoAplicacion.Empresa, true, "Admite el recibo de pago como adjunto opcional del mismo documento (2026-10-17). Mensual — el documento de un periodo (p. ej. 01/05) vence 3 meses después (01/08), porque tarda en emitirse con la fecha del periodo ya pasada."),
        (new Guid("20000000-0000-0000-0000-000000000005"), "Recibo de pago RLC/TC1", 3, true, 20, AmbitoAplicacion.Empresa, true, "Archivado el 2026-10-17: el recibo es adjunto opcional de «RLC (TC1)», que lo sustituye."),
        (new Guid("20000000-0000-0000-0000-000000000006"), "RLC/TC1 + Recibo de pago", 3, true, 21, AmbitoAplicacion.Empresa, true, "Archivado el 2026-10-17: la variante combinada es «RLC (TC1)» con el recibo como adjunto opcional, que lo sustituye."),
        (new Guid("20000000-0000-0000-0000-000000000007"), "RNT", 3, true, 22, AmbitoAplicacion.Empresa, true, "Mismo criterio que el RLC."),
        (new Guid("20000000-0000-0000-0000-000000000008"), "Mutua de Accidentes", null, false, 23, AmbitoAplicacion.Empresa, true, "Vigencia sin especificar — fecha de vencimiento manual."),
        (new Guid("20000000-0000-0000-0000-000000000009"), "Seguro de Responsabilidad Civil", null, false, 24, AmbitoAplicacion.Empresa, true, "Admite el recibo de pago como adjunto opcional del mismo documento (2026-10-17). Vigencia sin especificar — fecha de vencimiento manual."),
        (new Guid("2000000A-0000-0000-0000-00000000000A"), "Servicio de Prevención Ajeno", null, false, 25, AmbitoAplicacion.Empresa, true, "Debe venir acompañado de un certificado de pago que indica la fecha fin de validez — se introduce esa fecha manualmente."),
        (new Guid("2000000B-0000-0000-0000-00000000000B"), "Evaluación de Riesgos Laborales", null, false, 26, AmbitoAplicacion.Empresa, true, "Vigencia sin especificar — fecha de vencimiento manual."),
        (new Guid("2000000C-0000-0000-0000-00000000000C"), "Planificación de la Actividad Preventiva", null, false, 27, AmbitoAplicacion.Empresa, true, "Vigencia sin especificar — fecha de vencimiento manual."),
        (new Guid("2000000D-0000-0000-0000-00000000000D"), "Tarjeta de identificación fiscal", null, false, 28, AmbitoAplicacion.Empresa, false, "Opcional — no obligatorio para todos los clientes."),

        // --- Ampliación del catálogo de Empresa (Fase 37, 2026-07, a partir de un listado de documentación CAE/PRL de España pasado por el usuario) ---
        (new Guid("40000000-0000-0000-0000-000000000001"), "Plan de Prevención", null, false, 29, AmbitoAplicacion.Empresa, true, "Vigente con revisiones — vencimiento manual."),
        (new Guid("40000000-0000-0000-0000-000000000002"), "Designación de Recursos Preventivos", null, false, 30, AmbitoAplicacion.Empresa, true, "Vigente hasta modificación — vencimiento manual."),
        (new Guid("40000000-0000-0000-0000-000000000003"), "Procedimiento de Coordinación de Actividades Empresariales", null, false, 31, AmbitoAplicacion.Empresa, false, "Vigente hasta revisión — vencimiento manual. Deja de ser obligatorio el 2026-10-17 (decisión del propietario)."),
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
        // Project-Hydra-Negocio/CATALOGO_FORMATOS_PRL.md. F-107 ("Comunicación de desplazamiento a la
        // autoridad laboral") NO se da de alta aquí: ya existe como "Comunicación
        // de desplazamiento" (Id 40000011-...-000000000011, línea de arriba) —
        // verificado antes de añadir para no duplicar. Ninguno se marca
        // EsObligatorio=true por defecto pese a que F-93/F-95 no tienen umbral de
        // plantilla legal (Project-Hydra-Negocio/CATALOGO_FORMATOS_PRL.md § 5.5): activarlo por defecto
        // cambiaría de golpe el estado de cumplimiento de todos los centros
        // existentes sin que el propietario lo haya confirmado — queda a criterio
        // de cada Administrador vía /tipos-documento.
        (new Guid("60000000-0000-0000-0000-000000000001"), "Autorización de uso de equipo de trabajo/maquinaria", null, false, 38, AmbitoAplicacion.Trabajador, false, "F-27 del catálogo de formatos PRL — arts. 3.4 y 5 RD 1215/1997. Vigente hasta modificación — vencimiento manual."),
        (new Guid("60000000-0000-0000-0000-000000000002"), "Acta de presencia del recurso preventivo", null, false, 46, AmbitoAplicacion.Empresa, false, "F-41 del catálogo de formatos PRL — práctica probatoria del art. 32 bis.3 LPRL. Un acta por presencia — vencimiento manual."),
        (new Guid("60000000-0000-0000-0000-000000000003"), "Acta de reunión de coordinación", null, false, 47, AmbitoAplicacion.Empresa, false, "F-47 del catálogo de formatos PRL — art. 11.b y 11.c RD 171/2004. Un acta por reunión — vencimiento manual."),
        (new Guid("60000000-0000-0000-0000-000000000004"), "Informe de investigación de accidente o incidente", null, false, 48, AmbitoAplicacion.Empresa, false, "F-70 del catálogo de formatos PRL — art. 16.3 LPRL. Un informe por accidente o incidente — vencimiento manual."),
        (new Guid("60000000-0000-0000-0000-000000000005"), "Protocolo frente al acoso sexual y por razón de sexo", null, false, 49, AmbitoAplicacion.Empresa, false, "F-93 del catálogo de formatos PRL — art. 48 LO 3/2007. Obligatorio para todas las empresas, sin umbral de plantilla. Vigente hasta revisión — vencimiento manual. Archivado el 2026-10-17 (retirado del catálogo base, sin sustituto)."),
        (new Guid("60000000-0000-0000-0000-000000000006"), "Registro retributivo", null, false, 50, AmbitoAplicacion.Empresa, false, "F-95 del catálogo de formatos PRL — RD 902/2020. Obligatorio para todas las empresas, sin umbral de plantilla. Vigente hasta revisión — vencimiento manual."),

        // --- Tramo 1.2 del MVP-1 de formatos (2026-08-14): al construir la
        // plantilla impresa de F-44/F-52/F-50/F-29, se detectó que tampoco
        // tenían TipoDocumento (Project-Hydra-Negocio/CATALOGO_FORMATOS_PRL.md § 9.2 clasifica solo
        // F-53 como "tipo existente" para este tramo — F-44/F-52/F-50/F-29
        // habían quedado fuera de esa clasificación por error, verificado en
        // TipoDocumentoSeedData.cs antes de asumirlo). F-53 ("Declaración
        // Responsable CAE") no se repite aquí: ya existe (Id
        // 4000000C-...-00000000000C, línea de arriba).
        (new Guid("70000000-0000-0000-0000-000000000001"), "Información de riesgos propios aportados al centro", null, false, 51, AmbitoAplicacion.Empresa, false, "F-44 del catálogo de formatos PRL — art. 4.2 RD 171/2004. El formato Outbound por excelencia: lo emite el contratista hacia el titular del centro. Vigente hasta modificación de los riesgos — vencimiento manual."),
        (new Guid("70000000-0000-0000-0000-000000000002"), "Información y coordinación con trabajadores autónomos", null, false, 52, AmbitoAplicacion.Empresa, false, "F-52 del catálogo de formatos PRL — art. 24.5 LPRL · art. 4.1 RD 171/2004. Vigente hasta modificación — vencimiento manual."),
        (new Guid("70000000-0000-0000-0000-000000000003"), "Registro del deber de vigilancia sobre subcontratas", null, false, 53, AmbitoAplicacion.Empresa, false, "F-50 del catálogo de formatos PRL — art. 24.3 LPRL · art. 10 RD 171/2004. El único de la familia D que la ley impone directamente al contratista principal sobre sus Subcontratas. Un registro por subcontrata/verificación — vencimiento manual."),
        (new Guid("70000000-0000-0000-0000-000000000004"), "Recibí de normas, procedimientos y plan de emergencia", null, false, 39, AmbitoAplicacion.Trabajador, false, "F-29 del catálogo de formatos PRL — arts. 18 y 20 LPRL. Vigente hasta modificación de normas/plan — vencimiento manual."),

        // --- Tres tipos reales de construcción y autónomos (2026-10-15, decisión
        // del propietario). Ninguno es EsObligatorio=true: el REA y el Libro de
        // Subcontratación se activan solo en obra de construcción, así que son
        // RequisitoDocumental.Condicional (ver RequeridoDe) — el mismo patrón que
        // "Designación de Recursos Preventivos" — y no cuentan para el
        // cumplimiento de quien no pisa una obra; el recibo de RETA queda
        // opcional (Requerido=No) y lo pide quien lo necesite. ---
        (new Guid("80000000-0000-0000-0000-000000000001"), "Certificado de inscripción en el REA", 36, true, 54, AmbitoAplicacion.Empresa, false, "Registro de Empresas Acreditadas (Ley 32/2006 y RD 1109/2007). Solo aplica a obras de construcción. La inscripción tiene validez de 3 años y se renueva."),
        (new Guid("80000000-0000-0000-0000-000000000002"), "Libro de Subcontratación", null, false, 1, AmbitoAplicacion.Proyecto, false, "Art. 8 Ley 32/2006. Uno por obra: lo lleva el contratista principal y recoge cada subcontratación. Sin caducidad mientras dure la obra."),
        (new Guid("80000000-0000-0000-0000-000000000003"), "Recibo de cuota de autónomos", 1, true, 55, AmbitoAplicacion.Empresa, false, "Justificante mensual de pago de la cuota del Régimen Especial de Trabajadores Autónomos. El autónomo es su propia Empresa."),

        // --- Huecos del catálogo aprobados el 2026-10-17 (revisión del catálogo y respuesta del experto CAE).
        // Ninguno es obligatorio por defecto: el alta en el RETA la pide quien la necesite, el plan de emergencia
        // del contratista no lo exige el art. 10.2 RD 171/2004, y el documento genérico del Cliente empresarial
        // depende de cada Cliente empresarial. ---
        (new Guid("80000000-0000-0000-0000-000000000004"), "Alta en el RETA", null, false, 56, AmbitoAplicacion.Empresa, false, "Resolución de alta del autónomo en el Régimen Especial de Trabajadores Autónomos: prueba la afiliación, mientras que el recibo de cuota prueba el pago. El autónomo es su propia Empresa. Sin caducidad propia — vencimiento manual."),
        (new Guid("80000000-0000-0000-0000-000000000005"), "Plan de emergencia y evacuación", null, false, 57, AmbitoAplicacion.Empresa, false, "Plan de emergencia y evacuación de la Empresa. Opcional y nunca obligatorio por defecto: el art. 10.2 RD 171/2004 no lo exige al contratista. Vigente hasta revisión — vencimiento manual."),
        (new Guid("80000000-0000-0000-0000-000000000006"), "Documento requerido por el Cliente empresarial", null, false, 58, AmbitoAplicacion.Empresa, false, "Tipo genérico para los formularios propios que pide un Cliente empresarial concreto. Vigencia según cada Cliente empresarial — vencimiento manual."),
    ];

    /// <summary>
    /// Grupo de cada tipo del catálogo (aprobado el 2026-10-17, ver <see cref="GrupoTipoDocumento"/>). Los tipos de
    /// Vehículo y de Proyecto no figuran: se agrupan por su ámbito. El ratchet
    /// <c>GruposDelCatalogoSemillaTests</c> exige que todo tipo de Trabajador y de Empresa tenga grupo.
    /// </summary>
    internal static readonly Dictionary<Guid, GrupoTipoDocumento> GrupoPorId = ConstruirGrupos();

    private static Dictionary<Guid, GrupoTipoDocumento> ConstruirGrupos()
    {
        var grupos = new Dictionary<Guid, GrupoTipoDocumento>();
        void Poner(GrupoTipoDocumento grupo, params string[] ids)
        {
            foreach (var id in ids) grupos.Add(new Guid(id), grupo);
        }

        Poner(GrupoTipoDocumento.IdentidadYExtranjeria,
            "50000012-0000-0000-0000-000000000012", "50000013-0000-0000-0000-000000000013",
            "50000014-0000-0000-0000-000000000014", "50000015-0000-0000-0000-000000000015",
            "50000016-0000-0000-0000-000000000016");
        Poner(GrupoTipoDocumento.VigilanciaDeLaSalud, "10000000-0000-0000-0000-000000000001");
        Poner(GrupoTipoDocumento.RelacionLaboral, "50000000-0000-0000-0000-000000000001", "50000000-0000-0000-0000-000000000002");
        Poner(GrupoTipoDocumento.InformacionYEpi,
            "10000000-0000-0000-0000-000000000002", "10000000-0000-0000-0000-000000000008", "70000000-0000-0000-0000-000000000004");
        Poner(GrupoTipoDocumento.FormacionPrl,
            "10000000-0000-0000-0000-000000000003", "10000000-0000-0000-0000-000000000004", "10000000-0000-0000-0000-000000000005",
            "10000000-0000-0000-0000-000000000006", "10000000-0000-0000-0000-000000000007", "10000000-0000-0000-0000-00000000000C",
            "10000000-0000-0000-0000-00000000000D", "50000000-0000-0000-0000-000000000003", "50000000-0000-0000-0000-000000000004");
        Poner(GrupoTipoDocumento.HabilitacionesYOficios,
            "10000000-0000-0000-0000-000000000009", "10000000-0000-0000-0000-00000000000A", "10000000-0000-0000-0000-00000000000B",
            "10000000-0000-0000-0000-00000000000E", "10000000-0000-0000-0000-00000000000F", "50000000-0000-0000-0000-000000000005",
            "50000000-0000-0000-0000-000000000006", "50000000-0000-0000-0000-000000000007", "50000000-0000-0000-0000-000000000008",
            "50000000-0000-0000-0000-000000000009", "5000000A-0000-0000-0000-00000000000A", "5000000B-0000-0000-0000-00000000000B",
            "5000000C-0000-0000-0000-00000000000C", "5000000D-0000-0000-0000-00000000000D", "5000000E-0000-0000-0000-00000000000E",
            "5000000F-0000-0000-0000-00000000000F", "50000010-0000-0000-0000-000000000010", "50000011-0000-0000-0000-000000000011",
            "60000000-0000-0000-0000-000000000001");
        Poner(GrupoTipoDocumento.SeguridadSocialYHacienda,
            "20000000-0000-0000-0000-000000000001", "20000000-0000-0000-0000-000000000002", "20000000-0000-0000-0000-000000000003",
            "20000000-0000-0000-0000-000000000004", "20000000-0000-0000-0000-000000000005", "20000000-0000-0000-0000-000000000006",
            "20000000-0000-0000-0000-000000000007", "80000000-0000-0000-0000-000000000003", "80000000-0000-0000-0000-000000000004");
        Poner(GrupoTipoDocumento.SegurosYMutua, "20000000-0000-0000-0000-000000000008", "20000000-0000-0000-0000-000000000009");
        Poner(GrupoTipoDocumento.OrganizacionPreventiva,
            "2000000A-0000-0000-0000-00000000000A", "2000000B-0000-0000-0000-00000000000B", "2000000C-0000-0000-0000-00000000000C",
            "40000000-0000-0000-0000-000000000001", "40000000-0000-0000-0000-000000000002", "40000000-0000-0000-0000-000000000004",
            "40000000-0000-0000-0000-000000000005", "40000000-0000-0000-0000-000000000006", "60000000-0000-0000-0000-000000000002",
            "80000000-0000-0000-0000-000000000005");
        Poner(GrupoTipoDocumento.CoordinacionCae,
            "40000000-0000-0000-0000-000000000003", "4000000C-0000-0000-0000-00000000000C", "60000000-0000-0000-0000-000000000003",
            "60000000-0000-0000-0000-000000000004", "70000000-0000-0000-0000-000000000001", "70000000-0000-0000-0000-000000000002",
            "70000000-0000-0000-0000-000000000003", "80000000-0000-0000-0000-000000000006");
        Poner(GrupoTipoDocumento.SocietariaYAdministrativa,
            "2000000D-0000-0000-0000-00000000000D", "40000000-0000-0000-0000-000000000007", "40000000-0000-0000-0000-000000000008",
            "4000000D-0000-0000-0000-00000000000D");
        Poner(GrupoTipoDocumento.Certificaciones,
            "40000000-0000-0000-0000-000000000009", "4000000A-0000-0000-0000-00000000000A", "4000000B-0000-0000-0000-00000000000B");
        Poner(GrupoTipoDocumento.EmpresaExtranjera,
            "4000000E-0000-0000-0000-00000000000E", "4000000F-0000-0000-0000-00000000000F", "40000010-0000-0000-0000-000000000010",
            "40000011-0000-0000-0000-000000000011");
        Poner(GrupoTipoDocumento.Igualdad, "60000000-0000-0000-0000-000000000005", "60000000-0000-0000-0000-000000000006");
        Poner(GrupoTipoDocumento.Construccion, "80000000-0000-0000-0000-000000000001");
        return grupos;
    }

    /// <summary>
    /// Datos adicionales que admite cada tipo (aprobado el 2026-10-17, lista del mockup de Documentos §5–§7): horas en
    /// los tipos de formación, nivel en la Formación PRL de convenio, recibo adjunto en RLC (TC1) y en el Seguro de
    /// Responsabilidad Civil, y estado «Renuncia» en el Certificado de aptitud médica.
    /// </summary>
    internal static readonly Dictionary<Guid, DatosAdicionalesDocumento> DatosAdicionalesPorId = new()
    {
        [new Guid("10000000-0000-0000-0000-000000000001")] = DatosAdicionalesDocumento.Renuncia,
        [new Guid("10000000-0000-0000-0000-000000000003")] = DatosAdicionalesDocumento.Horas,
        [new Guid("10000000-0000-0000-0000-000000000004")] = DatosAdicionalesDocumento.Horas,
        [new Guid("10000000-0000-0000-0000-000000000005")] = DatosAdicionalesDocumento.Horas | DatosAdicionalesDocumento.Nivel,
        [new Guid("10000000-0000-0000-0000-000000000006")] = DatosAdicionalesDocumento.Horas,
        [new Guid("10000000-0000-0000-0000-000000000007")] = DatosAdicionalesDocumento.Horas,
        [new Guid("10000000-0000-0000-0000-000000000009")] = DatosAdicionalesDocumento.Horas,
        [new Guid("10000000-0000-0000-0000-00000000000B")] = DatosAdicionalesDocumento.Horas,
        [new Guid("10000000-0000-0000-0000-00000000000D")] = DatosAdicionalesDocumento.Horas,
        [new Guid("10000000-0000-0000-0000-00000000000E")] = DatosAdicionalesDocumento.Horas,
        [new Guid("10000000-0000-0000-0000-00000000000F")] = DatosAdicionalesDocumento.Horas,
        [new Guid("50000000-0000-0000-0000-000000000003")] = DatosAdicionalesDocumento.Horas,
        [new Guid("20000000-0000-0000-0000-000000000004")] = DatosAdicionalesDocumento.ReciboAdjunto,
        [new Guid("20000000-0000-0000-0000-000000000009")] = DatosAdicionalesDocumento.ReciboAdjunto,
    };

    /// <summary>
    /// Tipos retirados del catálogo base (2026-10-17) y su sustituto, o nulo si no lo tienen. Retirar no borra nada:
    /// los Documentos existentes conservan su tipo, que deja de ofrecerse en altas nuevas y de pedirse de serie.
    /// </summary>
    internal static readonly Dictionary<Guid, Guid?> ArchivadosConSustituto = new()
    {
        // «Recibo de pago RLC/TC1» y «RLC/TC1 + Recibo de pago»: un solo tipo «RLC (TC1)» con el recibo como adjunto.
        [new Guid("20000000-0000-0000-0000-000000000005")] = new Guid("20000000-0000-0000-0000-000000000004"),
        [new Guid("20000000-0000-0000-0000-000000000006")] = new Guid("20000000-0000-0000-0000-000000000004"),
        // «Formación EPIs»: lo sustituye «Entrega de EPI».
        [new Guid("50000000-0000-0000-0000-000000000004")] = new Guid("10000000-0000-0000-0000-000000000002"),
        // «Protocolo frente al acoso sexual y por razón de sexo»: sin sustituto.
        [new Guid("60000000-0000-0000-0000-000000000005")] = null,
    };

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
        new Guid("20000000-0000-0000-0000-000000000007"), // RNT
    ];

    /// <summary>
    /// Tipos de Empresa que son documentos oficiales de la Administración
    /// con validación automática (verificación de firma + parser, ver
    /// PerfilDocumentoOficial). El "Recibo de pago RLC/TC1" suelto queda
    /// fuera: es un justificante bancario, no un documento sellado por la
    /// TGSS. La variante combinada comparte parser con "RLC" — si la
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
    /// Aliases del catálogo, por Id — precondición de T3 (PR #313, campo de
    /// nombres alternativos) que este incremento por fin usa: cada tipo cuyo
    /// nombre se limpió de contaminación (§2bis, patrones A/B/C/D/E) conserva
    /// aquí el nombre contaminado anterior, y en los casos de acrónimo/alias
    /// histórico (D, E) también la sigla suelta — para que nada que ya busque
    /// o filtre por el nombre antiguo deje de encontrar la fila. Solo entran
    /// los Id efectivamente renombrados en este incremento; el resto del
    /// catálogo no tenía nombre contaminado o su patrón (F, G, H) queda fuera
    /// de alcance a propósito (ver el docstring de la clase).
    /// </summary>
    private static readonly Dictionary<Guid, string[]> AliasesOriginalesPorId = new()
    {
        // Bloque original de T3 (2026-08-29). Su orden y su contenido fijan los Id 90000000-…-000000000001 a …016 de
        // AliasesParaMigracion: no se reordena ni se amplía; los alias nuevos van en AliasesNuevosPorId.
        [new Guid("10000000-0000-0000-0000-000000000001")] = ["Apto médico laboral"],
        [new Guid("10000000-0000-0000-0000-000000000002")] = ["EPIS (firma)"],
        [new Guid("50000016-0000-0000-0000-000000000016")] = ["DNI o NIE en vigor", "DNI/NIE/TIE"],
        [new Guid("20000000-0000-0000-0000-000000000004")] = ["RLC/TC1", "TC1"],
        [new Guid("20000000-0000-0000-0000-000000000007")] = ["RNT/TC2", "TC2"],
        [new Guid("2000000A-0000-0000-0000-00000000000A")] = ["SPA (Servicio de Prevención Ajeno)", "SPA"],
        [new Guid("2000000B-0000-0000-0000-00000000000B")] = ["EVR (Evaluación de Riesgos Laborales)", "EVR"],
        [new Guid("2000000C-0000-0000-0000-00000000000C")] = ["PAP (Planificación de la Actividad Preventiva)", "PAP"],
        [new Guid("2000000D-0000-0000-0000-00000000000D")] = ["Tarjeta CIF", "CIF"],
    };

    /// <summary>
    /// Alias añadidos con la revisión del catálogo del 2026-10-17: el nombre anterior de cada tipo renombrado y los
    /// sinónimos habituales. Van en su propio bloque de Id (90000001-…, ver <see cref="AliasesParaMigracion"/>) y la
    /// migración los inserta con ON CONFLICT DO NOTHING, para no chocar con un alias igual que un Administrador ya
    /// hubiera añadido a mano.
    /// </summary>
    internal static readonly Dictionary<Guid, string[]> AliasesNuevosPorId = new()
    {
        // Trabajador
        [new Guid("10000000-0000-0000-0000-000000000001")] = ["Reconocimiento médico", "Apto"],
        [new Guid("10000000-0000-0000-0000-000000000002")] = ["EPI"],
        [new Guid("10000000-0000-0000-0000-000000000003")] = ["Reciclaje 4h"],
        [new Guid("10000000-0000-0000-0000-000000000004")] = ["Formación Art. 19", "Formación en prevención", "Art. 19"],
        [new Guid("10000000-0000-0000-0000-000000000005")] = ["Formación 60h (base convenio)", "Formación 60h", "Recurso preventivo 60h", "Nivel básico PRL 60h"],
        [new Guid("10000000-0000-0000-0000-000000000006")] = ["Formación 20h"],
        [new Guid("10000000-0000-0000-0000-000000000007")] = ["Formación 6h"],
        [new Guid("10000000-0000-0000-0000-000000000008")] = ["Información Art. 18", "Registro de información PRL", "Art. 18"],
        [new Guid("10000000-0000-0000-0000-00000000000A")] = ["Plataforma elevadora", "Plataforma elevadora móvil"],
        [new Guid("10000000-0000-0000-0000-00000000000B")] = ["LOTO (4h)"],
        [new Guid("10000000-0000-0000-0000-00000000000F")] = ["Trabajos en altura (8h)", "Altura", "Formación en altura"],
        [new Guid("50000000-0000-0000-0000-000000000002")] = ["TA.2", "TA2", "IDC", "Alta TA2", "Informe de datos de cotización"],
        [new Guid("50000000-0000-0000-0000-000000000005")] = ["Carne de conducir"],
        [new Guid("50000015-0000-0000-0000-000000000015")] = ["Certificado A1 de Seguridad Social", "A1", "Formulario A1"],
        [new Guid("50000016-0000-0000-0000-000000000016")] = ["DNI/NIE", "DNI", "NIE"],
        [new Guid("60000000-0000-0000-0000-000000000001")] = ["Autorización de uso de equipo de trabajo"],

        // Empresa
        [new Guid("20000000-0000-0000-0000-000000000001")] = ["Certificado SS", "Certificado TGSS", "Corriente SS", "Certificado de no deuda SS"],
        [new Guid("20000000-0000-0000-0000-000000000002")] = ["Certificado AEAT", "AEAT", "Corriente Hacienda", "Certificado de contratistas"],
        [new Guid("20000000-0000-0000-0000-000000000003")] = ["ITA", "Trabajadores en alta (CCC)"],
        [new Guid("20000000-0000-0000-0000-000000000004")] = ["RLC", "Recibo de pago RLC/TC1", "RLC/TC1 + Recibo de pago"],
        [new Guid("20000000-0000-0000-0000-000000000008")] = ["Mutua", "Certificado de mutua", "Mutua AT/EP"],
        [new Guid("20000000-0000-0000-0000-000000000009")] = ["Seguro de Responsabilidad Civil + recibo de pago", "Seguro RC + recibo", "Seguro RC"],
        [new Guid("40000000-0000-0000-0000-000000000006")] = ["Certificado SPA"],
        [new Guid("80000000-0000-0000-0000-000000000001")] = ["REA"],
        [new Guid("80000000-0000-0000-0000-000000000003")] = ["Recibo de cuota de autónomos (RETA)", "RETA"],
        [new Guid("80000000-0000-0000-0000-000000000004")] = ["Alta de autónomo", "Alta RETA"],
        [new Guid("80000000-0000-0000-0000-000000000005")] = ["Plan de emergencia", "Plan de evacuación"],

        // Vehículo
        [new Guid("30000000-0000-0000-0000-000000000001")] = ["ITV"],
        [new Guid("30000000-0000-0000-0000-000000000003")] = ["Seguro del vehículo", "Póliza del vehículo"],
    };

    /// <summary>Todos los alias de cada tipo, originales primero: lo que recibe la copia del catálogo de un Tenant nuevo.</summary>
    internal static readonly Dictionary<Guid, string[]> AliasesPorId = AliasesOriginalesPorId.Keys
        .Concat(AliasesNuevosPorId.Keys)
        .Distinct()
        .ToDictionary(
            id => id,
            id => AliasesOriginalesPorId.GetValueOrDefault(id, []).Concat(AliasesNuevosPorId.GetValueOrDefault(id, [])).ToArray());


    /// <summary>
    /// Copia editable del catálogo completo para un tenant nuevo
    /// (Project-Hydra-Negocio/tecnico/docs/MULTITENANCY.md § 7) con los flags de IA aplicados — la usan
    /// DelegacionDemoSeeder y SegundoTenantSeeder para no repetir la
    /// construcción (y para que ningún aprovisionamiento vuelva a olvidarse
    /// de los flags, como pasó con los tenants de demo).
    /// </summary>

    /// <summary>
    /// Naturaleza jurídica de cada tipo del catálogo — <b>con qué autoridad
    /// se pide</b>, eje independiente de si se pide (ver
    /// <see cref="NaturalezaJuridica"/>). Búsqueda por nombre, mismo patrón
    /// que <c>PerfilOficialDe</c>.
    ///
    /// <para>
    /// <b>Solo figura aquí lo verificado contra fuente oficial.</b> Todo lo
    /// demás cae en <see cref="NaturalezaJuridica.RequisitoCliente"/>, que es
    /// la afirmación más débil y la única segura: dice «alguien lo pide», que
    /// es cierto de todo el catálogo, y no atribuye ninguna ley. Sub-afirmar
    /// se corrige; sobre-afirmar es el fallo que esta taxonomía existe para
    /// impedir.
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>Hueco declarado</b>: muchas formaciones y habilitaciones de
    /// máquina (carretillas, PEMP, grúas, altura…) son con toda probabilidad
    /// <see cref="NaturalezaJuridica.ObligacionCondicionada"/> por el RD
    /// 1215/1997 y por convenio, pero <b>no se han verificado una por una</b>
    /// y por eso no se afirma. Igual con el protocolo de acoso y el registro
    /// retributivo. Es trabajo de verificación pendiente, no una conclusión.
    /// </para>
    /// </summary>
    private static NaturalezaJuridica NaturalezaDe(string nombre) => nombre switch
    {
        // — Obligación legal directa: la norma lo exige por escrito y sin condición —
        // Art. 10 RD 171/2004: lo único que se exige por escrito en concurrencia.
        "Evaluación de Riesgos Laborales" => NaturalezaJuridica.ObligacionLegal,
        "Planificación de la Actividad Preventiva" => NaturalezaJuridica.ObligacionLegal,
        // Art. 16 LPRL: el plan de prevención y tener modalidad preventiva válida.
        "Plan de Prevención" => NaturalezaJuridica.ObligacionLegal,
        "Modalidad Preventiva" => NaturalezaJuridica.ObligacionLegal,
        // Arts. 18 y 19 LPRL: informar y formar son obligaciones materiales.
        // Ojo: la obligación es EL HECHO; el certificado es prueba, y la LPRL
        // no fija caducidad — la renovación periódica sale de convenio.
        "Información de riesgos del puesto" => NaturalezaJuridica.ObligacionLegal,
        "Formación PRL del puesto" => NaturalezaJuridica.ObligacionLegal,

        // — Obligación condicionada: la norma la exige en un supuesto concreto —
        // Art. 22.1 LPRL: la vigilancia de la salud exige consentimiento del
        // trabajador. La renuncia es un estado del mismo documento
        // (Documento.EsRenuncia, 2026-10-17), que no cuenta como conforme:
        // no sustituye al apto donde la vigilancia es obligatoria.
        "Certificado de aptitud médica" => NaturalezaJuridica.ObligacionCondicionada,
        // Dependen del supuesto: recursos preventivos y procedimiento de CAE.
        "Designación de Recursos Preventivos" => NaturalezaJuridica.ObligacionCondicionada,
        "Procedimiento de Coordinación de Actividades Empresariales" => NaturalezaJuridica.ObligacionCondicionada,
        // Art. 43.1.f LGT: carga con efecto legal (excluye responsabilidad
        // subsidiaria), con caducidad real de 12 meses exigible en cada pago.
        "Certificado de estar al corriente con Hacienda" => NaturalezaJuridica.ObligacionCondicionada,
        // Ley 45/1999: desplazamiento transnacional.
        "Comunicación de desplazamiento" => NaturalezaJuridica.ObligacionCondicionada,
        // Ley 32/2006 y RD 1109/2007: REA y Libro de Subcontratación, en obra de construcción.
        "Certificado de inscripción en el REA" => NaturalezaJuridica.ObligacionCondicionada,
        "Libro de Subcontratación" => NaturalezaJuridica.ObligacionCondicionada,
        "Documento acreditativo de empresa extranjera" => NaturalezaJuridica.ObligacionCondicionada,
        "Certificado A1" => NaturalezaJuridica.ObligacionCondicionada,

        // — Práctica del sector: ninguna norma lo exige, lo piden todos —
        // RD 773/1997 obliga a PROPORCIONAR el EPI (art. 3.c); revisados los
        // arts. 1-10, ninguno exige registro firmado de entrega.
        "Entrega de EPI" => NaturalezaJuridica.PracticaSector,
        // El blindaje del art. 42.1 ET nace de que la principal lo solicite a
        // la TGSS, no de archivar el que envía el contratista. Se pide igual.
        "Certificado de estar al corriente con la Seguridad Social" => NaturalezaJuridica.PracticaSector,
        // Tener modalidad es obligación (arriba); entregar el concierto es práctica.
        "Servicio de Prevención Ajeno" => NaturalezaJuridica.PracticaSector,

        // Todo lo demás: la afirmación más débil y verdadera.
        _ => NaturalezaJuridica.RequisitoCliente,
    };

    /// <summary>
    /// Eje "Requerido" — ¿lo pedimos? Traducido MECÁNICAMENTE del booleano
    /// viejo como punto de partida (T1), con las excepciones de la tabla
    /// verificada de la taxonomía documental (T2,
    /// Project-Hydra-Negocio/tecnico/taxonomia-documental-cae-propuesta-2026-08-27.md §2): dos tipos que
    /// dependen del supuesto concreto pasan a Condicional, y dos que no son
    /// documentación CAE salen del baseline. El resto del catálogo no se
    /// toca — ni un tercero de estas cuatro filas cambia de naturaleza,
    /// solo de si se pide.
    /// </summary>
    /// <summary>
    /// Ámbito Trabajador: cada Documento de este tipo queda vinculado a un
    /// trabajador identificado (nombre, DNI) por definición del propio
    /// ámbito — es dato personal aunque su contenido no diga nada sobre
    /// salud (art. 4.1 RGPD: basta con identificar a la persona).
    /// </summary>
    private const string MotivoAmbitoTrabajador =
        "Ámbito Trabajador: el Documento queda vinculado a un trabajador identificado por definición del ámbito — es dato personal por sí mismo, sin que su contenido revele salud.";

    /// <summary>
    /// Ámbito Empresa o Vehículo cuyo contenido describe a la organización o
    /// al vehículo (certificación, cotización, evaluación, procedimiento,
    /// ficha técnica, seguro) y no nombra a ninguna persona física.
    /// </summary>
    private const string MotivoEmpresaOVehiculoSinNombrar =
        "El contenido describe a la Empresa o al Vehículo (certificación, cotización, evaluación, procedimiento, ficha técnica) y no identifica a ninguna persona física por su nombre.";

    /// <summary>
    /// <b>Propuesta</b> de sensibilidad documental del catálogo semilla —
    /// DEC-34/36 (REC-132): clasificación canónica compartida por REC-036
    /// (purga de derivados de IA) y REC-099 (auditoría de acceso), revisable
    /// por el propietario (documento espejo:
    /// <c>Project-Hydra-Negocio/tecnico/docs/SENSIBILIDAD-DOCUMENTAL.md</c> en el repositorio de
    /// negocio, estado <c>Draft</c>).
    ///
    /// <para>
    /// <b>Regla aplicada, no el nombre del tipo</b>: la categoría especial de
    /// salud cubre cualquier documento o dato derivado que revele
    /// información sobre salud física o mental de una persona identificada
    /// — aquí eso son, verificados uno por uno, el reconocimiento médico
    /// (ejemplo literal de la decisión) y el informe de accidente o
    /// incidente, que puede describir la lesión del trabajador. El resto de
    /// ámbito Trabajador es dato personal ordinario (identifica a la
    /// persona sin revelar su salud); el resto de ámbito Empresa/Vehículo es
    /// dato personal solo cuando su contenido nombra a alguien en concreto
    /// (una designación, un acta, un apoderamiento, un listado nominal) y no
    /// tiene datos personales cuando describe a la organización o al
    /// vehículo.
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>Hueco declarado</b>: "Informe de investigación de accidente o
    /// incidente" se clasifica como categoría especial de salud porque
    /// <i>puede</i> describir una lesión, no porque siempre lo haga — mismo
    /// criterio de honestidad evidencial que <see cref="NaturalezaDe"/>: no
    /// se ha revisado expediente por expediente, y ante la duda se aplica la
    /// lectura más protectora.
    /// </para>
    /// </summary>
    /// <summary>
    /// <c>internal</c> en vez de <c>private</c>: el ratchet de cobertura
    /// (<c>SensibilidadDelCatalogoSemillaTests</c>, en
    /// <c>CaeManager.IntegrationTests</c> vía <c>InternalsVisibleTo</c>)
    /// necesita distinguir "clasificado explícitamente" de "cayó en el valor
    /// por defecto", algo que consultar solo <see cref="SensibilidadDe"/> no
    /// permite cuando el resultado coincide con el default.
    /// </summary>
    internal static readonly Dictionary<string, (SensibilidadDocumental Valor, string Motivo)> SensibilidadPorNombre = new()
    {
        // --- Categoría especial de salud: el contenido revela salud física o mental de una persona identificada ---
        ["Certificado de aptitud médica"] = (SensibilidadDocumental.CategoriaEspecialSalud,
            "Resultado de un reconocimiento médico (art. 22.1 LPRL): revela aptitud/no aptitud derivada de datos de salud del trabajador. Ejemplo literal de DEC-34/36."),
        ["Informe de investigación de accidente o incidente"] = (SensibilidadDocumental.CategoriaEspecialSalud,
            "Puede describir la lesión o el estado de salud del trabajador accidentado — no verificado expediente por expediente, así que se aplica la lectura más protectora (ver 'Hueco declarado')."),

        // --- Datos personales — ámbito Trabajador (identifica a la persona sin revelar salud) ---
        ["Entrega de EPI"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Reciclaje PRL"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Formación PRL del puesto"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Formación PRL de convenio"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Formación PRL de convenio por oficio"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Formación PRL de convenio por oficio, módulo específico"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Información de riesgos del puesto"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Carretillas elevadoras"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["PEMP (plataformas elevadoras)"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Bloqueo y etiquetado (LOTO)"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Seguridad alimentaria"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Primeros auxilios"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Espacios confinados"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Trabajos en altura"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Contrato de Trabajo"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Alta en Seguridad Social"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Formación Riesgos Específicos"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Formación EPIs"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Permiso de conducir"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Riesgo Eléctrico"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Manipulación Manual de Cargas"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Manipulación de Productos Químicos"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["ADR"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Soldadura"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Operador de Puente Grúa"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Operador de Grúa Torre"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Operador de Grúa Móvil"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Operador de Dumper"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Operador de Retroexcavadora"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Operador de Minicargadora"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Operador de Manipulador Telescópico"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Permiso de residencia"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Permiso de trabajo"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Certificado de Registro de Ciudadano de la UE"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Certificado A1"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Documento de identidad"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Autorización de uso de equipo de trabajo/maquinaria"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),
        ["Recibí de normas, procedimientos y plan de emergencia"] = (SensibilidadDocumental.DatosPersonales, MotivoAmbitoTrabajador),

        // --- Datos personales — ámbito Empresa (el contenido nombra a una persona física concreta) ---
        ["Informe de trabajadores en alta"] = (SensibilidadDocumental.DatosPersonales,
            "Informe con relación nominal de trabajadores en alta del centro: identifica a cada trabajador listado, aunque el trámite sea de ámbito Empresa — mismo motivo que activa DeteccionTrabajadoresActiva para este tipo."),
        ["RNT"] = (SensibilidadDocumental.DatosPersonales,
            "Relación Nominal de Trabajadores: listado nominal, mismo motivo que ITA."),
        ["Designación de Recursos Preventivos"] = (SensibilidadDocumental.DatosPersonales,
            "El contenido es precisamente nombrar a la persona designada como recurso preventivo."),
        ["Organigrama Preventivo"] = (SensibilidadDocumental.DatosPersonales,
            "Nombra a los responsables de cada función preventiva de la empresa."),
        ["Escritura de Constitución"] = (SensibilidadDocumental.DatosPersonales,
            "Acto público que identifica a los socios/fundadores por nombre y DNI como parte de su contenido legal."),
        ["Poder del Representante Legal"] = (SensibilidadDocumental.DatosPersonales,
            "El contenido es precisamente identificar a la persona apoderada."),
        ["Comunicación de desplazamiento"] = (SensibilidadDocumental.DatosPersonales,
            "Comunica qué trabajadores concretos se desplazan (Ley 45/1999): los identifica, aunque el trámite sea de ámbito Empresa."),
        ["Acta de presencia del recurso preventivo"] = (SensibilidadDocumental.DatosPersonales,
            "Registra qué persona concreta estuvo presente como recurso preventivo, y cuándo."),
        ["Acta de reunión de coordinación"] = (SensibilidadDocumental.DatosPersonales,
            "El acta nombra a los asistentes a la reunión de coordinación."),
        ["Registro retributivo"] = (SensibilidadDocumental.DatosPersonales,
            "RD 902/2020: retribución desglosada por trabajador y sexo — identifica a cada persona con su salario, sin revelar salud."),
        ["Información y coordinación con trabajadores autónomos"] = (SensibilidadDocumental.DatosPersonales,
            "Identifica al trabajador autónomo con el que se coordina."),
        ["Libro de Subcontratación"] = (SensibilidadDocumental.DatosPersonales,
            "Art. 8 Ley 32/2006: recoge, por cada subcontratación, la identificación de los representantes legales y de las personas que intervienen en la obra."),
        ["Recibo de cuota de autónomos"] = (SensibilidadDocumental.DatosPersonales,
            "El autónomo es su propia Empresa: el recibo lo identifica como persona física (nombre, DNI, cuenta de cargo), sin revelar salud."),
        ["Alta en el RETA"] = (SensibilidadDocumental.DatosPersonales,
            "El autónomo es su propia Empresa: la resolución de alta lo identifica como persona física (nombre, DNI, actividad), sin revelar salud."),
        ["Plan de emergencia y evacuación"] = (SensibilidadDocumental.DatosPersonales,
            "Suele nombrar a los integrantes de los equipos de emergencia y evacuación de la Empresa, sin revelar salud."),

        // --- Contenido indeterminado: se aplica la lectura más protectora ---
        ["Documento requerido por el Cliente empresarial"] = (SensibilidadDocumental.CategoriaEspecialSalud,
            "Tipo genérico para formularios propios de cada Cliente empresarial: su contenido no se conoce de antemano y puede incluir datos de salud, así que se aplica la lectura más protectora (mismo criterio que el Hueco declarado del informe de accidente)."),

        // --- Sin datos personales — ámbito Empresa (el contenido describe a la organización, no a una persona) ---
        ["Certificado de inscripción en el REA"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar + " Acredita la inscripción de la Empresa en el Registro de Empresas Acreditadas."),
        ["Certificado de estar al corriente con la Seguridad Social"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar),
        ["Certificado de estar al corriente con Hacienda"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar),
        ["RLC (TC1)"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar + " El listado nominal es el RNT, tipo distinto."),
        ["Recibo de pago RLC/TC1"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar),
        ["RLC/TC1 + Recibo de pago"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar),
        ["Mutua de Accidentes"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar + " Identifica la aseguradora contratada por la empresa, no la salud de ningún trabajador concreto."),
        ["Seguro de Responsabilidad Civil"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar),
        ["Servicio de Prevención Ajeno"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar + " El concierto con el SPA no reproduce reconocimientos médicos individuales."),
        ["Evaluación de Riesgos Laborales"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar + " Evalúa riesgos por puesto de trabajo, no a trabajadores concretos."),
        ["Planificación de la Actividad Preventiva"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar),
        ["Tarjeta de identificación fiscal"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar),
        ["Plan de Prevención"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar),
        ["Procedimiento de Coordinación de Actividades Empresariales"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar),
        ["Política Preventiva"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar),
        ["Modalidad Preventiva"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar),
        ["ISO 45001"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar),
        ["ISO 9001"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar),
        ["ISO 14001"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar),
        ["Declaración Responsable CAE"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar + " Declara el cumplimiento de la empresa; la firma del representante es incidental, no el contenido."),
        ["Relación de Maquinaria"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar + " Listado de máquinas, no de personas."),
        ["VAT europeo"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar),
        ["Documento acreditativo de empresa extranjera"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar),
        ["Traducción jurada"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar + " Traduce documentación de la empresa; el contenido traducido no cambia esta clasificación de base."),
        ["Protocolo frente al acoso sexual y por razón de sexo"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar + " Es el protocolo institucional, no un expediente de caso concreto."),
        ["Información de riesgos propios aportados al centro"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar),
        ["Registro del deber de vigilancia sobre subcontratas"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar + " Vigilancia sobre Subcontratas (empresas), no sobre personas concretas."),

        // --- Sin datos personales — ámbito Vehículo (el contenido describe el vehículo) ---
        ["ITC"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar),
        ["Ficha técnica"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar),
        ["Seguro"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar),
        ["Autorización de circulación"] = (SensibilidadDocumental.SinDatosPersonales, MotivoEmpresaOVehiculoSinNombrar),
    };

    /// <summary>
    /// Traduce el nombre a la sensibilidad propuesta — <b>único punto</b>,
    /// junto con <see cref="TipoDocumento.RevelaSalud"/>, donde el nombre de
    /// un tipo decide algo sobre salud. Para cualquier nombre que no esté en
    /// <see cref="SensibilidadPorNombre"/> (un tipo nuevo del catálogo, o uno
    /// creado por un tenant), el valor por defecto ya es el más protector
    /// desde el constructor de <see cref="TipoDocumento"/> — esta función
    /// devuelve el mismo valor por coherencia, nunca uno más débil.
    /// </summary>
    private static SensibilidadDocumental SensibilidadDe(string nombre) =>
        SensibilidadPorNombre.TryGetValue(nombre, out var entrada) ? entrada.Valor : SensibilidadDocumental.CategoriaEspecialSalud;

    private static RequisitoDocumental RequeridoDe(Guid id, string nombre, bool esObligatorio) => ArchivadosConSustituto.ContainsKey(id)
        // Un tipo archivado nunca se pide de serie: se pide su sustituto (ver TipoDocumento.Archivar).
        ? RequisitoDocumental.No
        : nombre switch
        {
            // Depende del supuesto concreto (nivel 2, no baseline): el art. 10
            // RD 171/2004 no lo exige incondicionalmente para toda empresa. El
            // «Procedimiento de Coordinación de Actividades Empresariales» estaba
            // aquí también hasta el 2026-10-17, cuando dejó de ser obligatorio
            // (EsObligatorio = false, así que cae en No por la regla general).
            "Designación de Recursos Preventivos" => RequisitoDocumental.Condicional,
            // Solo se activan en obra de construcción (REA: Ley 32/2006 y RD
            // 1109/2007; Libro de Subcontratación: art. 8 Ley 32/2006).
            "Certificado de inscripción en el REA" => RequisitoDocumental.Condicional,
            "Libro de Subcontratación" => RequisitoDocumental.Condicional,
            // Es cotización y aseguramiento, no prevención — sale del baseline CAE.
            "Mutua de Accidentes" => RequisitoDocumental.No,
            // Ley 45/1999 art. 6.5 exige documentación traducida, no jurada —
            // exigir la jurada es requisito de cliente, no baseline. Ya estaba en
            // No por el booleano viejo; queda explícito para que no dependa de
            // que nadie active el flag de origen por error.
            "Traducción jurada" => RequisitoDocumental.No,

            _ => esObligatorio ? RequisitoDocumental.Si : RequisitoDocumental.No,
        };

    /// <summary>Grupo del tipo base, o nulo para Vehículo, Proyecto y cualquier Id que no esté en <see cref="GrupoPorId"/>.</summary>
    public static GrupoTipoDocumento? GrupoDe(Guid id) => GrupoPorId.TryGetValue(id, out var grupo) ? grupo : null;

    public static DatosAdicionalesDocumento DatosAdicionalesDe(Guid id) =>
        DatosAdicionalesPorId.GetValueOrDefault(id, DatosAdicionalesDocumento.Ninguno);

    public static IEnumerable<TipoDocumento> CrearCopiasParaTenant()
    {
        // Dos pasadas: el sustituto de un tipo archivado es la COPIA de este mismo Tenant, no la fila base del Tenant #1,
        // así que primero se crean todas las copias y después se archiva cada una apuntando a la copia de su sustituto.
        var copias = new Dictionary<Guid, TipoDocumento>();
        foreach (var t in Datos)
        {
            var copia = new TipoDocumento(
                t.Nombre, t.VigenciaMeses, t.AplicaVencimiento, t.Orden, t.Ambito,
                RequeridoDe(t.Id, t.Nombre, t.EsObligatorio),
                NaturalezaDe(t.Nombre), t.Notas,
                sensibilidad: SensibilidadDe(t.Nombre));
            copia.EstablecerDeteccionTrabajadoresActiva(TieneDeteccionTrabajadores(t.Nombre));
            copia.EstablecerPerfilDocumentoOficial(PerfilOficialDe(t.Nombre));
            copia.EstablecerGrupo(GrupoDe(t.Id));
            copia.EstablecerDatosAdicionales(DatosAdicionalesDe(t.Id));
            if (AliasesPorId.TryGetValue(t.Id, out var aliases))
                copia.EstablecerAliases(aliases);
            copias.Add(t.Id, copia);
        }

        foreach (var (id, sustitutoId) in ArchivadosConSustituto)
            copias[id].Archivar(sustitutoId is { } s ? copias[s] : null);

        return copias.Values;
    }

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
            Requerido = RequeridoDe(d.Id, d.Nombre, d.EsObligatorio),
            Naturaleza = NaturalezaDe(d.Nombre),
            Sensibilidad = SensibilidadDe(d.Nombre),
            LecturaIaActiva = true,
            DeteccionTrabajadoresActiva = IdsConDeteccionTrabajadores.Contains(d.Id),
            // Empieza desactivada para todo el catálogo semilla — mismo
            // criterio que DeteccionTrabajadoresActiva: el Administrador la
            // activa explícitamente solo donde interesa (ver Issue #19).
            VerificacionIaActiva = false,
            PerfilDocumentoOficial = PerfilesOficiales.GetValueOrDefault(d.Id, PerfilDocumentoOficial.Ninguno),
            // Serie por tipo de contratación: todos, el comportamiento anterior a la decisión de 2026-10-16.
            TiposContratacionDeSerie = CaeManager.Domain.Trabajadores.TiposContratacion.Todos,
            Grupo = GrupoDe(d.Id),
            DatosAdicionales = DatosAdicionalesDe(d.Id),
            Archivado = ArchivadosConSustituto.ContainsKey(d.Id),
            SustitutoId = ArchivadosConSustituto.GetValueOrDefault(d.Id),
            // El catálogo semilla pertenece al tenant #1 (ver Etapa 2 de
            // Project-Hydra-Negocio/tecnico/PLAN-MIGRACION-MULTITENANT.md) — un tenant nuevo recibirá su
            // propia copia editable al aprovisionarse (Project-Hydra-Negocio/tecnico/docs/MULTITENANCY.md § 7),
            // no una referencia a esta misma fila.
            TenantId = TenantSeedData.IdPorDefecto
        });

    /// <summary>
    /// Proyección plana de <see cref="AliasesPorId"/> para el
    /// <c>HasData</c> de <see cref="TipoDocumentoAlias"/> — mismo criterio
    /// que <see cref="ComoFilasParaMigracion"/>: el catálogo semilla
    /// pertenece al tenant #1, y estos son los únicos alias que existían
    /// antes de que el Administrador pueda editar nada. Id deterministas para
    /// que la migración sea reproducible: el bloque original de T3 conserva el
    /// prefijo 90000000 (16 filas, sin reordenar), y los alias de la revisión
    /// del 2026-10-17 usan su propio prefijo 90000001, de modo que ningún
    /// alias ya sembrado cambia de Id.
    /// </summary>
    public static IEnumerable<object> AliasesParaMigracion() =>
        FilasDeAlias(AliasesOriginalesPorId, "90000000").Concat(FilasDeAlias(AliasesNuevosPorId, "90000001"));

    private static IEnumerable<object> FilasDeAlias(Dictionary<Guid, string[]> aliasesPorId, string prefijo)
    {
        var indice = 0;
        foreach (var (tipoDocumentoId, aliases) in aliasesPorId)
        {
            foreach (var texto in aliases)
            {
                indice++;
                yield return new
                {
                    Id = new Guid($"{prefijo}-0000-0000-0000-{indice:D12}"),
                    TipoDocumentoId = tipoDocumentoId,
                    Texto = texto,
                    TenantId = TenantSeedData.IdPorDefecto
                };
            }
        }
    }
}
