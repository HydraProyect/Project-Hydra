using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarSensibilidadDocumental : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // El valor por defecto de la columna es el más protector
            // (CategoriaEspecialSalud), no una cadena vacía sin sentido:
            // cualquier fila que ya exista y cuyo Nombre no aparezca en la
            // clasificación de abajo —un tipo que un tenant creó por su
            // cuenta— no tiene propuesta todavía, y perder en silencio si
            // revela salud sería exactamente el fallo que DEC-34/36 pide
            // evitar.
            migrationBuilder.AddColumn<string>(
                name: "Sensibilidad",
                table: "TiposDocumento",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "CategoriaEspecialSalud");

            // Traducción de la propuesta de TipoDocumentoSeedData.SensibilidadPorNombre,
            // por NOMBRE y no por Id: a diferencia de T1 (PartirEsObligatorioEnRequeridoYNaturaleza),
            // que solo tenía que reponer el catálogo semilla del tenant #1,
            // aquí SegundoTenantSeeder y DelegacionDemoSeeder ya pueden haber
            // sembrado copias completas del catálogo con sus propios Ids
            // generados en tenants ya existentes — un UPDATE solo por Id de
            // tenant #1 habría dejado esas copias ancladas para siempre en el
            // valor por defecto, aunque su Nombre coincida exactamente con un
            // tipo ya clasificado. ELSE preserva el valor (el default) para
            // cualquier Nombre que no esté en esta lista.
            migrationBuilder.Sql("""
                UPDATE "TiposDocumento"
                SET "Sensibilidad" = CASE "Nombre"
                    WHEN 'Certificado de aptitud médica' THEN 'CategoriaEspecialSalud'
                    WHEN 'Informe de investigación de accidente o incidente' THEN 'CategoriaEspecialSalud'
                    WHEN 'Entrega de EPI' THEN 'DatosPersonales'
                    WHEN 'Reciclaje 4h' THEN 'DatosPersonales'
                    WHEN 'Formación Art. 19' THEN 'DatosPersonales'
                    WHEN 'Formación 60h (base convenio)' THEN 'DatosPersonales'
                    WHEN 'Formación 20h' THEN 'DatosPersonales'
                    WHEN 'Formación 6h' THEN 'DatosPersonales'
                    WHEN 'Información Art. 18' THEN 'DatosPersonales'
                    WHEN 'Carretillas elevadoras' THEN 'DatosPersonales'
                    WHEN 'PEMP (plataformas elevadoras)' THEN 'DatosPersonales'
                    WHEN 'LOTO (4h)' THEN 'DatosPersonales'
                    WHEN 'Seguridad alimentaria' THEN 'DatosPersonales'
                    WHEN 'Primeros auxilios' THEN 'DatosPersonales'
                    WHEN 'Espacios confinados' THEN 'DatosPersonales'
                    WHEN 'Trabajos en altura (8h)' THEN 'DatosPersonales'
                    WHEN 'Contrato de Trabajo' THEN 'DatosPersonales'
                    WHEN 'Alta en Seguridad Social' THEN 'DatosPersonales'
                    WHEN 'Formación Riesgos Específicos' THEN 'DatosPersonales'
                    WHEN 'Formación EPIs' THEN 'DatosPersonales'
                    WHEN 'Permiso de conducir' THEN 'DatosPersonales'
                    WHEN 'Riesgo Eléctrico' THEN 'DatosPersonales'
                    WHEN 'Manipulación Manual de Cargas' THEN 'DatosPersonales'
                    WHEN 'Manipulación de Productos Químicos' THEN 'DatosPersonales'
                    WHEN 'ADR' THEN 'DatosPersonales'
                    WHEN 'Soldadura' THEN 'DatosPersonales'
                    WHEN 'Operador de Puente Grúa' THEN 'DatosPersonales'
                    WHEN 'Operador de Grúa Torre' THEN 'DatosPersonales'
                    WHEN 'Operador de Grúa Móvil' THEN 'DatosPersonales'
                    WHEN 'Operador de Dumper' THEN 'DatosPersonales'
                    WHEN 'Operador de Retroexcavadora' THEN 'DatosPersonales'
                    WHEN 'Operador de Minicargadora' THEN 'DatosPersonales'
                    WHEN 'Operador de Manipulador Telescópico' THEN 'DatosPersonales'
                    WHEN 'Permiso de residencia' THEN 'DatosPersonales'
                    WHEN 'Permiso de trabajo' THEN 'DatosPersonales'
                    WHEN 'Certificado de Registro de Ciudadano de la UE' THEN 'DatosPersonales'
                    WHEN 'Certificado A1 de Seguridad Social' THEN 'DatosPersonales'
                    WHEN 'Documento de identidad' THEN 'DatosPersonales'
                    WHEN 'Autorización de uso de equipo de trabajo' THEN 'DatosPersonales'
                    WHEN 'Recibí de normas, procedimientos y plan de emergencia' THEN 'DatosPersonales'
                    WHEN 'ITA' THEN 'DatosPersonales'
                    WHEN 'RNT' THEN 'DatosPersonales'
                    WHEN 'Designación de Recursos Preventivos' THEN 'DatosPersonales'
                    WHEN 'Organigrama Preventivo' THEN 'DatosPersonales'
                    WHEN 'Escritura de Constitución' THEN 'DatosPersonales'
                    WHEN 'Poder del Representante Legal' THEN 'DatosPersonales'
                    WHEN 'Comunicación de desplazamiento' THEN 'DatosPersonales'
                    WHEN 'Acta de presencia del recurso preventivo' THEN 'DatosPersonales'
                    WHEN 'Acta de reunión de coordinación' THEN 'DatosPersonales'
                    WHEN 'Registro retributivo' THEN 'DatosPersonales'
                    WHEN 'Información y coordinación con trabajadores autónomos' THEN 'DatosPersonales'
                    WHEN 'Certificado de estar al corriente con la Seguridad Social' THEN 'SinDatosPersonales'
                    WHEN 'Certificado de estar al corriente con Hacienda' THEN 'SinDatosPersonales'
                    WHEN 'RLC' THEN 'SinDatosPersonales'
                    WHEN 'Recibo de pago RLC/TC1' THEN 'SinDatosPersonales'
                    WHEN 'RLC/TC1 + Recibo de pago' THEN 'SinDatosPersonales'
                    WHEN 'Mutua' THEN 'SinDatosPersonales'
                    WHEN 'Seguro de Responsabilidad Civil + recibo de pago' THEN 'SinDatosPersonales'
                    WHEN 'Servicio de Prevención Ajeno' THEN 'SinDatosPersonales'
                    WHEN 'Evaluación de Riesgos Laborales' THEN 'SinDatosPersonales'
                    WHEN 'Planificación de la Actividad Preventiva' THEN 'SinDatosPersonales'
                    WHEN 'Tarjeta de identificación fiscal' THEN 'SinDatosPersonales'
                    WHEN 'Plan de Prevención' THEN 'SinDatosPersonales'
                    WHEN 'Procedimiento de Coordinación de Actividades Empresariales' THEN 'SinDatosPersonales'
                    WHEN 'Política Preventiva' THEN 'SinDatosPersonales'
                    WHEN 'Modalidad Preventiva' THEN 'SinDatosPersonales'
                    WHEN 'ISO 45001' THEN 'SinDatosPersonales'
                    WHEN 'ISO 9001' THEN 'SinDatosPersonales'
                    WHEN 'ISO 14001' THEN 'SinDatosPersonales'
                    WHEN 'Declaración Responsable CAE' THEN 'SinDatosPersonales'
                    WHEN 'Relación de Maquinaria' THEN 'SinDatosPersonales'
                    WHEN 'VAT europeo' THEN 'SinDatosPersonales'
                    WHEN 'Documento acreditativo de empresa extranjera' THEN 'SinDatosPersonales'
                    WHEN 'Traducción jurada' THEN 'SinDatosPersonales'
                    WHEN 'Protocolo frente al acoso sexual y por razón de sexo' THEN 'SinDatosPersonales'
                    WHEN 'Información de riesgos propios aportados al centro' THEN 'SinDatosPersonales'
                    WHEN 'Registro del deber de vigilancia sobre subcontratas' THEN 'SinDatosPersonales'
                    WHEN 'ITC' THEN 'SinDatosPersonales'
                    WHEN 'Ficha técnica' THEN 'SinDatosPersonales'
                    WHEN 'Seguro' THEN 'SinDatosPersonales'
                    WHEN 'Autorización de circulación' THEN 'SinDatosPersonales'
                    ELSE "Sensibilidad"
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Sensibilidad",
                table: "TiposDocumento");
        }
    }
}
