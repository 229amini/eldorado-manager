using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;

namespace SellerDesk
{
    /// <summary>One sheet of a workbook: a bold header row, then rows of text, numbers and dates.</summary>
    public sealed class Sheet
    {
        public string Name = "Sheet";
        public List<string> Header = new List<string>();
        public List<object[]> Rows = new List<object[]>();
        public List<double> Widths = new List<double>();
    }

    /// <summary>
    /// Writes a plain .xlsx that Excel, LibreOffice and Google Sheets open. It is a zip of a few XML files,
    /// so it needs no library: strings go inline, numbers keep two decimals, dates get a date format.
    /// </summary>
    public static class Xlsx
    {
        public static void Write(string path, IList<Sheet> sheets)
        {
            if (File.Exists(path)) File.Delete(path);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                Add(zip, "[Content_Types].xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                    "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                    "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                    string.Concat(sheets.Select((_, i) => $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>")) +
                    "</Types>");
                Add(zip, "_rels/.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                    "</Relationships>");
                Add(zip, "xl/workbook.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" +
                    string.Concat(sheets.Select((s, i) => $"<sheet name=\"{Escape(SheetName(s.Name))}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) +
                    "</sheets></workbook>");
                Add(zip, "xl/_rels/workbook.xml.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    string.Concat(sheets.Select((_, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>")) +
                    $"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                    "</Relationships>");
                // Styles: 0 plain, 1 bold header, 2 money 0.00, 3 date, 4 bold money for totals.
                Add(zip, "xl/styles.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                    "<numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"yyyy-mm-dd hh:mm\"/></numFmts>" +
                    "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                    "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
                    "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                    "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                    "<cellXfs count=\"5\">" +
                    "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                    "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
                    "<xf numFmtId=\"2\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
                    "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
                    "<xf numFmtId=\"2\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\"/>" +
                    "</cellXfs></styleSheet>");
                for (var i = 0; i < sheets.Count; i++) Add(zip, $"xl/worksheets/sheet{i + 1}.xml", SheetXml(sheets[i]));
            }
        }

        /// <summary>A total row: its numbers come out bold.</summary>
        public sealed class Total
        {
            public double Value;
            public Total(double value) => Value = value;
        }

        private static string SheetXml(Sheet sheet)
        {
            var xml = new StringBuilder();
            xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            xml.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            xml.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
            if (sheet.Widths.Count > 0)
            {
                xml.Append("<cols>");
                for (var c = 0; c < sheet.Widths.Count; c++)
                    xml.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{sheet.Widths[c].ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
                xml.Append("</cols>");
            }
            xml.Append("<sheetData>");
            var rows = new List<object[]> { sheet.Header.Cast<object>().ToArray() };
            rows.AddRange(sheet.Rows);
            for (var r = 0; r < rows.Count; r++)
            {
                xml.Append($"<row r=\"{r + 1}\">");
                for (var c = 0; c < rows[r].Length; c++) xml.Append(Cell(Column(c) + (r + 1), rows[r][c], r == 0));
                xml.Append("</row>");
            }
            xml.Append("</sheetData></worksheet>");
            return xml.ToString();
        }

        private static string Cell(string at, object value, bool header)
        {
            switch (value)
            {
                case null:
                    return "";
                case Total total:
                    return $"<c r=\"{at}\" s=\"4\"><v>{total.Value.ToString("R", CultureInfo.InvariantCulture)}</v></c>";
                case DateTime moment:
                    // Excel counts days from 1899-12-30, in local time, which is what the seller reads.
                    return $"<c r=\"{at}\" s=\"3\"><v>{moment.ToLocalTime().ToOADate().ToString("R", CultureInfo.InvariantCulture)}</v></c>";
                case int whole:
                    return $"<c r=\"{at}\"><v>{whole}</v></c>";
                case double number:
                    return $"<c r=\"{at}\" s=\"2\"><v>{number.ToString("R", CultureInfo.InvariantCulture)}</v></c>";
                default:
                    var style = header ? " s=\"1\"" : "";
                    return $"<c r=\"{at}\" t=\"inlineStr\"{style}><is><t xml:space=\"preserve\">{Escape(Convert.ToString(value, CultureInfo.InvariantCulture))}</t></is></c>";
            }
        }

        private static string Column(int index)
        {
            var name = "";
            for (index++; index > 0; index = (index - 1) / 26) name = (char)('A' + (index - 1) % 26) + name;
            return name;
        }

        // Excel refuses sheet names over 31 characters or with any of : \ / ? * [ ]
        private static string SheetName(string name)
        {
            var clean = new string(name.Where(ch => ":\\/?*[]".IndexOf(ch) < 0).ToArray()).Trim();
            if (clean.Length == 0) clean = "Sheet";
            return clean.Length > 31 ? clean.Substring(0, 31) : clean;
        }

        private static string Escape(string text) =>
            SecurityElement.Escape(new string((text ?? "").Where(ch => ch == '\t' || ch == '\n' || ch == '\r' || ch >= ' ').ToArray()));

        private static void Add(ZipArchive zip, string name, string content)
        {
            using (var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false))) writer.Write(content);
        }
    }
}
