using Autodesk.AutoCAD;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Geometry;
using System;
using System.Windows.Forms;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Xml.XPath;
using Autodesk.AutoCAD.Windows;
using System.Xml;

namespace Mavat
{
    public class Class1
    {
        static Document doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        static Database dataBase = doc.Database;
        static Dictionary<string, string> trpToMavat = new Dictionary<string, string>();
        static Dictionary<string, string> layToMavat = new Dictionary<string, string>();
        static Dictionary<string, string> layerToMavat = new Dictionary<string, string>();

        [CommandMethod("OCT")]
        public static void OpenConvertTableWindow()
        {
            System.Windows.Window uc = new ConvertTableWindow();
            Autodesk.AutoCAD.ApplicationServices.Application.ShowModalWindow(uc);
        }
        [CommandMethod("Mavat")]
        public static void Convert()
        {
            using(Transaction trans = dataBase.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = GetBlockTable();
                BlockTableRecord modelSpace = GetModelSpace(blockTable);
                LayerTable layerTable = GetLayerTable();

                ScanDrawing(blockTable, modelSpace, layerTable);

                trans.Commit();
            }
        }
        private static void ScanDrawing(BlockTable blockTable, BlockTableRecord modelSpace, LayerTable layerTable)
        {
            using(Transaction trans = dataBase.TransactionManager.StartTransaction())
            {
                foreach(ObjectId oid in modelSpace)
                {
                    var element = oid.GetObject(OpenMode.ForRead);

                    if(element.GetType() == typeof(BlockReference))
                    {
                        ReplaceBlock((BlockReference)element, blockTable, modelSpace);
                    }
                    else ChangeLayerToMavat(element, layerTable);
                }

                trans.Commit();
            }
        }
        private static void ChangeLayerToMavat(DBObject element, LayerTable layerTable)
        {
            XPathDocument xml = new XPathDocument("../../ConvertTable.xml");
            XPathNavigator nav = xml.CreateNavigator();

            string curLay = (element as Entity).Layer;
            string toLay;

            if((toLay = (string)nav.Evaluate($"string(Root/Layers/Layer[From = '{curLay}']/To)")) != "")
            {
                if(!layerTable.Has(toLay))
                {
                    using(Transaction trans = dataBase.TransactionManager.StartTransaction())
                    {
                        using(LayerTableRecord ltr = new LayerTableRecord())
                        {
                            ltr.Name = toLay;
                            layerTable.UpgradeOpen();
                            layerTable.Add(ltr);
                            trans.AddNewlyCreatedDBObject(ltr, true);
                        }

                        trans.Commit();
                    }
                }

                (element as Entity).UpgradeOpen();
                (element as Entity).Layer = toLay;
            }
        }
        private static void ReplaceBlock(BlockReference element, BlockTable blockTable, BlockTableRecord modelSpace)
        {
            // If entity is a TRP####(mapit) block replace it with a mavat(M####) block.
            if((Regex.IsMatch(element.Name, "TRP.")))
            {
                Dictionary<string, string> attDic = new Dictionary<string, string>();
                foreach(ObjectId oid in element.AttributeCollection)
                {
                    AttributeReference ar = (AttributeReference)oid.GetObject(OpenMode.ForRead);
                    if(ar.Tag == "HIGHTM") attDic.Add("HEIGHT", ar.TextString);
                    else attDic.Add(ar.Tag, ar.TextString);
                }

                if(!trpToMavat.Keys.Contains(element.Name))
                    QuaryConvertTable(element.Name, blockTable);

                using(Transaction trans = dataBase.TransactionManager.StartTransaction())
                {
                    try
                    {
                        // btr = BlockTableRecord for mavat block in database.
                        BlockTableRecord btr = (BlockTableRecord)blockTable[trpToMavat[element.Name]].GetObject(OpenMode.ForRead);

                        // Append mavat block to drawing.
                        using(BlockReference br = new BlockReference(element.Position, btr.ObjectId))
                        {
                            modelSpace.UpgradeOpen();
                            modelSpace.AppendEntity(br);
                            element.UpgradeOpen();
                            element.Erase();

                            // Add all attributes to new mavat block.
                            foreach(ObjectId id in btr)
                            {
                                var v = id.GetObject(OpenMode.ForRead);

                                if(v.GetType() == typeof(AttributeDefinition))
                                {
                                    using(AttributeReference attRef = new AttributeReference())
                                    {
                                        attRef.SetAttributeFromBlock((AttributeDefinition)v, br.BlockTransform);
                                        attRef.TextString = attDic[attRef.Tag];
                                        br.AttributeCollection.AppendAttribute(attRef);
                                        trans.AddNewlyCreatedDBObject(attRef, true);
                                    }
                                }
                            }

                            trans.AddNewlyCreatedDBObject(br, true);
                        }

                        trans.Commit();
                    }
                    catch(KeyNotFoundException e)
                    {
                        doc.Editor.WriteMessage($"{element.Name} does not exists in trpToMavat dictionary.\n");
                    }
                }
            }
        }
        private static void QuaryConvertTable(string trpBlock, BlockTable blockTable)
        {
            XPathDocument xml = new XPathDocument(@"..\..\ConvertTable.xml");
            XPathNavigator nav = xml.CreateNavigator();

            try
            {
                string to = (string)nav.Evaluate($"string(Root/Blocks/Block[From = '{trpBlock}']/To)");
                string path = (string)nav.Evaluate($"string(Root/Blocks/Block[From = '{trpBlock}']/Path)");
                ImportMBlock(path, blockTable);
                trpToMavat.Add(trpBlock, to);
            }
            catch(InvalidOperationException e) { doc.Editor.WriteMessage($"Quary for {trpBlock} returned no result.\n"); }
        }
        private static void ImportMBlock(string path, BlockTable blockTable)
        {
            using(Database mBlocksDb = new Database(false, true))
            {
                ObjectIdCollection oidc;

                mBlocksDb.ReadDwgFile(path, FileShare.Read, false, null);
                mBlocksDb.CloseInput(true);

                using(Transaction trans = mBlocksDb.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)mBlocksDb.BlockTableId.GetObject(OpenMode.ForRead);
                    BlockTableRecord modelSpace = (BlockTableRecord)bt[BlockTableRecord.ModelSpace].GetObject(OpenMode.ForRead);

                    using(BlockTableRecord btr = new BlockTableRecord())
                    {
                        bt.UpgradeOpen();
                        bt.Add(btr);
                        btr.Name = Regex.Match(path, @"M\d{4}").Value;

                        oidc = new ObjectIdCollection();
                        foreach(ObjectId oid in modelSpace)
                            oidc.Add(oid);

                        btr.AssumeOwnershipOf(oidc);
                        trans.AddNewlyCreatedDBObject(btr, true);

                        oidc = new ObjectIdCollection(new ObjectId[] { btr.ObjectId });
                    }

                    trans.Commit();
                }

                mBlocksDb.WblockCloneObjects(oidc, blockTable.ObjectId, new IdMapping(), DuplicateRecordCloning.Ignore, false);
            }
        }
        private static BlockTableRecord GetModelSpace(BlockTable blockTable)
        {
            return (BlockTableRecord)blockTable[BlockTableRecord.ModelSpace].GetObject(OpenMode.ForRead);
        }
        private static BlockTable GetBlockTable()
        {
            return (BlockTable)dataBase.BlockTableId.GetObject(OpenMode.ForRead);
        }
        private static LayerTable GetLayerTable()
        {
            return (LayerTable)dataBase.LayerTableId.GetObject(OpenMode.ForRead);
        }
    }
}
