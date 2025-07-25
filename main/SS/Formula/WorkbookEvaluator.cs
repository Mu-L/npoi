/* ====================================================================
   Licensed To the Apache Software Foundation (ASF) under one or more
   contributor license agreements.  See the NOTICE file distributed with
   this work for Additional information regarding copyright ownership.
   The ASF licenses this file To You under the Apache License, Version 2.0
   (the "License"); you may not use this file except in compliance with
   the License.  You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed To in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
==================================================================== */

using System.Diagnostics;
using NPOI.SS.Formula.Atp;

namespace NPOI.SS.Formula
{
    using System;
    using NPOI.SS.Formula.Eval;
    using NPOI.SS.Util;
    using NPOI.SS.Formula.Functions;
    using NPOI.SS.Formula.UDF;
    using System.Collections.Generic;
    using NPOI.SS.UserModel;
    using NPOI.SS.Formula.PTG;
    using NPOI.Util;
    using NPOI.SS.Formula.Function;
    using EnumsNET;

    /**
     * Evaluates formula cells.<p/>
     *
     * For performance reasons, this class keeps a cache of all previously calculated intermediate
     * cell values.  Be sure To call {@link #ClearCache()} if any workbook cells are Changed between
     * calls To evaluate~ methods on this class.<br/>
     *
     * For POI internal use only
     *
     * @author Josh Micich
     */
    public class WorkbookEvaluator
    {

        private readonly IEvaluationWorkbook _workbook;
        private EvaluationCache _cache;
        private int _workbookIx;

        private readonly IEvaluationListener _evaluationListener;
        private readonly Dictionary<IEvaluationSheet, int> _sheetIndexesBySheet;
        private readonly Dictionary<String, int> _sheetIndexesByName;
        private CollaboratingWorkbooksEnvironment _collaboratingWorkbookEnvironment;
        private readonly IStabilityClassifier _stabilityClassifier;
        private readonly AggregatingUDFFinder _udfFinder;

        private bool _ignoreMissingWorkbooks = false;

        public WorkbookEvaluator(IEvaluationWorkbook workbook, IStabilityClassifier stabilityClassifier, UDFFinder udfFinder)
            : this(workbook, null, stabilityClassifier, udfFinder)
        {

        }

        public WorkbookEvaluator(IEvaluationWorkbook workbook, IEvaluationListener evaluationListener, IStabilityClassifier stabilityClassifier, UDFFinder udfFinder)
        {
            _workbook = workbook;
            _evaluationListener = evaluationListener;
            _cache = new EvaluationCache(evaluationListener);
            _sheetIndexesBySheet = new Dictionary<IEvaluationSheet, int>();
            _sheetIndexesByName = new Dictionary<string, int>();
            _collaboratingWorkbookEnvironment = CollaboratingWorkbooksEnvironment.EMPTY;
            _workbookIx = 0;
            _stabilityClassifier = stabilityClassifier;

            AggregatingUDFFinder defaultToolkit = // workbook can be null in unit tests
                workbook == null ? null : (AggregatingUDFFinder)workbook.GetUDFFinder();
            if (defaultToolkit != null && udfFinder != null)
            {
                defaultToolkit.Add(udfFinder);
            }
            _udfFinder = defaultToolkit;
        }

        /**
         * also for debug use. Used in ToString methods
         */
        /* package */
        public String GetSheetName(int sheetIndex)
        {
            return _workbook.GetSheetName(sheetIndex);
        }

        public WorkbookEvaluator GetOtherWorkbookEvaluator(String workbookName)
        {
            return _collaboratingWorkbookEnvironment.GetWorkbookEvaluator(workbookName);
        }

        internal IEvaluationWorkbook Workbook
        {
            get
            {
                return _workbook;
            }
        }
        internal IEvaluationSheet GetSheet(int sheetIndex)
        {
            return _workbook.GetSheet(sheetIndex);
        }
        /* package */
        internal IEvaluationName GetName(String name, int sheetIndex)
        {
            IEvaluationName evalName = _workbook.GetName(name, sheetIndex);
            return evalName;
        }
        private static bool IsDebugLogEnabled()
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }

        private static bool IsInfoLogEnabled()
        {
#if TRACE
            return true;
#else
            return false;
#endif
        }
        private static void LogDebug(String s)
        {
            if (IsDebugLogEnabled())
            {
                Debug.WriteLine(s);
            }
        }
        private static void LogInfo(String s)
        {
            if (IsInfoLogEnabled())
            {
                Trace.WriteLine(s);
            }
        }

        /* package */
        public void AttachToEnvironment(CollaboratingWorkbooksEnvironment collaboratingWorkbooksEnvironment, EvaluationCache cache, int workbookIx)
        {
            _collaboratingWorkbookEnvironment = collaboratingWorkbooksEnvironment;
            _cache = cache;
            _workbookIx = workbookIx;
        }
        /* package */
        public CollaboratingWorkbooksEnvironment GetEnvironment()
        {
            return _collaboratingWorkbookEnvironment;
        }

        /* package */
        public void DetachFromEnvironment()
        {
            _collaboratingWorkbookEnvironment = CollaboratingWorkbooksEnvironment.EMPTY;
            _cache = new EvaluationCache(_evaluationListener);
            _workbookIx = 0;
        }
        /* package */
        public IEvaluationListener GetEvaluationListener()
        {
            return _evaluationListener;
        }

        /**
         * Should be called whenever there are Changes To input cells in the evaluated workbook.
         * Failure To call this method after changing cell values will cause incorrect behaviour
         * of the evaluate~ methods of this class
         */
        public void ClearAllCachedResultValues()
        {
            _cache.Clear();
            _sheetIndexesBySheet.Clear();
            _workbook.ClearAllCachedResultValues();
        }

        /**
         * Should be called To tell the cell value cache that the specified (value or formula) cell 
         * Has Changed.
         */
        public void NotifyUpdateCell(IEvaluationCell cell)
        {
            int sheetIndex = GetSheetIndex(cell.Sheet);
            _cache.NotifyUpdateCell(_workbookIx, sheetIndex, cell);
        }
        /**
         * Should be called To tell the cell value cache that the specified cell Has just been
         * deleted. 
         */
        public void NotifyDeleteCell(IEvaluationCell cell)
        {
            int sheetIndex = GetSheetIndex(cell.Sheet);
            _cache.NotifyDeleteCell(_workbookIx, sheetIndex, cell);
        }

        public int GetSheetIndex(IEvaluationSheet sheet)
        {
            int result = int.MinValue;
            if(_sheetIndexesBySheet.TryGetValue(sheet, out int value))
                result = value;
            if (result == int.MinValue)
            {
                int sheetIndex = _workbook.GetSheetIndex(sheet);
                if (sheetIndex < 0)
                {
                    throw new Exception("Specified sheet from a different book");
                }
                result = sheetIndex;
                _sheetIndexesBySheet[sheet] = result;
            }
            return result;
        }
        /* package */
        internal int GetSheetIndexByExternIndex(int externSheetIndex)
        {
            return _workbook.ConvertFromExternSheetIndex(externSheetIndex);
        }
        /**
         * Case-insensitive.
         * @return -1 if sheet with specified name does not exist
         */
        /* package */
        public int GetSheetIndex(String sheetName)
        {
            int result;
            if (_sheetIndexesByName.TryGetValue(sheetName, out int value))
            {
                result = value;
            }
            else
            {
                int sheetIndex = _workbook.GetSheetIndex(sheetName);
                if (sheetIndex < 0)
                {
                    return -1;
                }
                result = sheetIndex;
                _sheetIndexesByName[sheetName] = result;
            }
            return result;
        }

        public ValueEval Evaluate(IEvaluationCell srcCell)
        {
            int sheetIndex = GetSheetIndex(srcCell.Sheet);
            return EvaluateAny(srcCell, sheetIndex, srcCell.RowIndex, srcCell.ColumnIndex, new EvaluationTracker(_cache));
        }
        /**
  * Evaluate a formula outside a cell value, e.g. conditional format rules or data validation expressions
  * 
  * @param formula to evaluate
  * @param ref defines the optional sheet and row/column base for the formula, if it is relative
  * @return value
  */
        public ValueEval Evaluate(String formula, CellReference reference)
        {
            String sheetName = reference == null ? null : reference.SheetName;
            int sheetIndex;
            if (sheetName == null)
            {
                sheetIndex = -1; // workbook scope only
            }
            else
            {
                sheetIndex = Workbook.GetSheetIndex(sheetName);
            }
            int rowIndex = reference == null ? -1 : reference.Row;
            int colIndex = reference == null ? -1 : reference.Col;
            OperationEvaluationContext ec = new OperationEvaluationContext(
                    this,
                    Workbook,
                    sheetIndex,
                    rowIndex,
                    colIndex,
                    new EvaluationTracker(_cache)
                );
            Ptg[] ptgs = FormulaParser.Parse(formula, (IFormulaParsingWorkbook)Workbook, FormulaType.Cell, sheetIndex, rowIndex);
            return EvaluateNameFormula(ptgs, ec);
        }
        public ValueEval Evaluate(String formula, CellReference target, CellRangeAddressBase region)
        {
            return Evaluate(formula, target, region, FormulaType.Cell);
        }

        
        /**
         * Some expressions need to be evaluated in terms of an offset from the top left corner of a region,
         * such as some data validation and conditional format expressions, when those constraints apply
         * to contiguous cells.  When a relative formula is used, it must be evaluated by shifting by the target
         * offset position relative to the top left of the range.
         * <p>
         * Returns a ValueEval that may be one or more values, such as the allowed values for a data validation constraint.
         * 
         * @param formula
         * @param target cell context for the operation
         * @param region containing the cell
         * @return ValueEval for one or more values
         * @throws IllegalArgumentException if target does not define a sheet name to evaluate the formula on.
         */
        public ValueEval EvaluateList(String formula, CellReference target, CellRangeAddressBase region) {
            return Evaluate(formula, target, region, FormulaType.DataValidationList);
        }

        private ValueEval Evaluate(String formula, CellReference target, CellRangeAddressBase region, FormulaType formulaType)
        {
            String sheetName = target == null ? null : target.SheetName;
            if (sheetName == null) throw new ArgumentException("Sheet name is required");

            int sheetIndex = Workbook.GetSheetIndex(sheetName);
            Ptg[] ptgs = FormulaParser.Parse(formula, (IFormulaParsingWorkbook)Workbook, formulaType, sheetIndex, target.Row);

            AdjustRegionRelativeReference(ptgs, target, region);

            OperationEvaluationContext ec = new OperationEvaluationContext(this, Workbook, sheetIndex, target.Row, target.Col, new EvaluationTracker(_cache),
                formulaType.GetAttributes().Get<SingleValueAttribute>().IsSingleValue);
            return EvaluateNameFormula(ptgs, ec);
        }
        /// <summary>
        /// Adjust formula relative references by the offset between the start of the given region and the given target cell.
        /// </summary>
        /// <param name="ptgs"></param>
        /// <param name="target">cell within the region to use.</param>
        /// <param name="region">containing the cell</param>
        /// <returns>true if any Ptg references were shifted</returns>
        /// <exception cref="IndexOutOfRangeException">if the resulting shifted row/column indexes are over the document format limits</exception>
        /// <exception cref="ArgumentException">if target is not within region.</exception>
        protected bool AdjustRegionRelativeReference(Ptg[] ptgs, CellReference target, CellRangeAddressBase region)
        {
            if (!region.IsInRange(target))
            {
                throw new ArgumentException(target + " is not within " + region);
            }

            return AdjustRegionRelativeReference(ptgs, target.Row - region.FirstRow, target.Col - region.FirstColumn);
        }
    
        /// <summary>
        /// Adjust the formula relative cell references by a given delta
        /// </summary>
        /// <param name="ptgs"></param>
        /// <param name="deltaRow">target row offset from the top left cell of a region</param>
        /// <param name="deltaColumn">target column offset from the top left cell of a region</param>
        /// <returns>true if any Ptg references were shifted</returns>
        /// <exception cref="IndexOutOfRangeException">if the resulting shifted row/column indexes are over the document format limits</exception>
        /// <exception cref="ArgumentException">if either of the deltas are negative, as the assumption is we are shifting formulas
        /// relative to the top left cell of a region.
        /// </exception>
        protected bool AdjustRegionRelativeReference(Ptg[] ptgs, int deltaRow, int deltaColumn) 
        {
            if (deltaRow < 0) throw new ArgumentException("offset row must be positive");
            if (deltaColumn < 0) throw new ArgumentException("offset column must be positive");

            bool shifted = false;
            foreach(Ptg ptg in ptgs)
            {
                // base class for cell reference "things"
                if (ptg is RefPtgBase reference) {
                    // re-calculate cell references
                SpreadsheetVersion version = _workbook.GetSpreadsheetVersion();
                if (reference.IsRowRelative)
                {
                    int rowIndex = reference.Row + deltaRow;
                    if (rowIndex > version.MaxRows)
                    {
                        throw new IndexOutOfRangeException(version.Name + " files can only have " + version.MaxRows + " rows, but row " + rowIndex + " was requested.");
                    }
                        reference.Row = rowIndex;
                    shifted = true;
                }
                if (reference.IsColRelative)
                {
                    int colIndex = reference.Column + deltaColumn;
                    if (colIndex > version.MaxColumns)
                    {
                        throw new IndexOutOfRangeException(version.Name + " files can only have " + version.MaxColumns + " columns, but column " + colIndex + " was requested.");
                    }
                    reference.Column = colIndex;
                    shifted = true;
                }
            }
        }
        return shifted;
    }
    /**
     * @return never <c>null</c>, never {@link BlankEval}
     */
    private ValueEval EvaluateAny(IEvaluationCell srcCell, int sheetIndex,
                    int rowIndex, int columnIndex, EvaluationTracker tracker)
        {
            bool shouldCellDependencyBeRecorded = _stabilityClassifier == null ? true
                    : !_stabilityClassifier.IsCellFinal(sheetIndex, rowIndex, columnIndex);
            ValueEval result;
            if (srcCell == null || srcCell.CellType != CellType.Formula)
            {
                result = GetValueFromNonFormulaCell(srcCell);
                if (shouldCellDependencyBeRecorded)
                {
                    tracker.AcceptPlainValueDependency(_workbookIx, sheetIndex, rowIndex, columnIndex, result);
                }
                return result;
            }

            FormulaCellCacheEntry cce = _cache.GetOrCreateFormulaCellEntry(srcCell);
            if (shouldCellDependencyBeRecorded || cce.IsInputSensitive)
            {
                tracker.AcceptFormulaDependency(cce);
            }
            IEvaluationListener evalListener = _evaluationListener;
            if (cce.GetValue() == null)
            {
                if (!tracker.StartEvaluate(cce))
                {
                    return ErrorEval.CIRCULAR_REF_ERROR;
                }
                OperationEvaluationContext ec = new OperationEvaluationContext(this, _workbook, sheetIndex, rowIndex, columnIndex, tracker);

                try
                {
                    Ptg[] ptgs = _workbook.GetFormulaTokens(srcCell);
                    if (evalListener == null)
                    {
                        result = EvaluateFormula(ec, ptgs);
                    }
                    else
                    {
                        evalListener.OnStartEvaluate(srcCell, cce);
                        result = EvaluateFormula(ec, ptgs);
                        evalListener.OnEndEvaluate(cce, result);
                    }

                    tracker.UpdateCacheResult(result);
                }
                catch (NotImplementedException e)
                {
                    throw AddExceptionInfo(e, sheetIndex, rowIndex, columnIndex);
                }
                catch (RuntimeException re)
                {
                    if (re.InnerException is WorkbookNotFoundException && _ignoreMissingWorkbooks)
                    {
                        LogInfo(re.InnerException.Message + " - Continuing with cached value!");
                        switch (srcCell.CachedFormulaResultType)
                        {
                            case CellType.Numeric:
                                result = new NumberEval(srcCell.NumericCellValue);
                                break;
                            case CellType.String:
                                result = new StringEval(srcCell.StringCellValue);
                                break;
                            case CellType.Blank:
                                result = BlankEval.instance;
                                break;
                            case CellType.Boolean:
                                result = BoolEval.ValueOf(srcCell.BooleanCellValue);
                                break;
                            case CellType.Error:
                                result = ErrorEval.ValueOf(srcCell.ErrorCellValue);
                                break;
                            case CellType.Formula:
                            default:
                                throw new RuntimeException("Unexpected cell type '" + srcCell.CellType + "' found!");
                        }
                    }
                    else
                    {
                        throw;
                    }
                }
                finally
                {
                    tracker.EndEvaluate(cce);
                }
            }
            else
            {
                if (evalListener != null)
                {
                    evalListener.OnCacheHit(sheetIndex, rowIndex, columnIndex, cce.GetValue());
                }
                return cce.GetValue();
            }
            if (IsDebugLogEnabled())
            {
                String sheetName = GetSheetName(sheetIndex);
                CellReference cr = new CellReference(rowIndex, columnIndex);
                LogDebug("Evaluated " + sheetName + "!" + cr.FormatAsString() + " To " + cce.GetValue());
            }
            // Usually (result === cce.getValue())
            // But sometimes: (result==ErrorEval.CIRCULAR_REF_ERROR, cce.getValue()==null)
            // When circular references are detected, the cache entry is only updated for
            // the top evaluation frame
            //return cce.GetValue();
            return result;
        }


        /**
         * Adds the current cell reference to the exception for easier debugging.
         * Would be nice to get the formula text as well, but that seems to require
         * too much digging around and casting to get the FormulaRenderingWorkbook.
         */
        private NotImplementedException AddExceptionInfo(NotImplementedException inner, int sheetIndex, int rowIndex, int columnIndex)
        {
            try
            {
                String sheetName = _workbook.GetSheetName(sheetIndex);
                CellReference cr = new CellReference(sheetName, rowIndex, columnIndex, false, false);
                String msg = "Error evaluating cell " + cr.FormatAsString();
                return new NotImplementedException(msg, inner);
            }
            catch
            {
                // avoid bombing out during exception handling
                //e.printStackTrace();
                return inner; // preserve original exception
            }
        }
        /**
         * Gets the value from a non-formula cell.
         * @param cell may be <c>null</c>
         * @return {@link BlankEval} if cell is <c>null</c> or blank, never <c>null</c>
         */
        /* package */
        internal static ValueEval GetValueFromNonFormulaCell(IEvaluationCell cell)
        {
            if (cell == null)
            {
                return BlankEval.instance;
            }
            CellType cellType = cell.CellType;
            switch (cellType)
            {
                case CellType.Numeric:
                    return new NumberEval(cell.NumericCellValue);
                case CellType.String:
                    return new StringEval(cell.StringCellValue);
                case CellType.Boolean:
                    return BoolEval.ValueOf(cell.BooleanCellValue);
                case CellType.Blank:
                    return BlankEval.instance;
                case CellType.Error:
                    return ErrorEval.ValueOf(cell.ErrorCellValue);
            }
            throw new Exception("Unexpected cell type (" + cellType + ")");
        }

        /**
         * whether print detailed messages about the next formula evaluation
         */
        private bool dbgEvaluationOutputForNextEval = false;

        // special logger for formula evaluation output (because of possibly very large output)
        private readonly POILogger EVAL_LOG = POILogFactory.GetLogger("POI.FormulaEval");
        // current indent level for evalution; negative value for no output
        private int dbgEvaluationOutputIndent = -1;

        // visibility raised for testing
        /* package */
        public ValueEval EvaluateFormula(OperationEvaluationContext ec, Ptg[] ptgs)
        {
            String dbgIndentStr = "";		// always init. to non-null just for defensive avoiding NPE
            if (dbgEvaluationOutputForNextEval)
            {
                // first evaluation call when ouput is desired, so iit. this evaluator instance
                dbgEvaluationOutputIndent = 1;
                dbgEvaluationOutputForNextEval = false;
            }
            if (dbgEvaluationOutputIndent > 0)
            {
                // init. indent string to needed spaces (create as substring vom very long space-only string;
                // limit indendation for deep recursions)
                dbgIndentStr = "                                                                                                    ";
                dbgIndentStr = dbgIndentStr.Substring(0, Math.Min(dbgIndentStr.Length, dbgEvaluationOutputIndent * 2));
                EVAL_LOG.Log(POILogger.WARN, dbgIndentStr
                                   + "- evaluateFormula('" + ec.GetRefEvaluatorForCurrentSheet().SheetNameRange
                                   + "'/" + new CellReference(ec.RowIndex, ec.ColumnIndex).FormatAsString()
                                   + "): " + Arrays.ToString(ptgs).Replace("\\Qorg.apache.poi.ss.formula.ptg.\\E", ""));
                dbgEvaluationOutputIndent++;
            }
           
            IEvaluationSheet evalSheet = ec.GetWorkbook().GetSheet(ec.SheetIndex);
            IEvaluationCell evaluationCell = evalSheet.GetCell(ec.RowIndex, ec.ColumnIndex);

            Stack<ValueEval> stack = new Stack<ValueEval>();
            for (int i = 0, iSize = ptgs.Length; i < iSize; i++)
            {

                // since we don't know how To handle these yet :(
                Ptg ptg = ptgs[i];
                if (dbgEvaluationOutputIndent > 0)
                {
                    EVAL_LOG.Log(POILogger.INFO, dbgIndentStr + "  * ptg " + i + ": " + ptg.ToString()+", stack:"+stack);
                }
                if (ptg is AttrPtg attrPtg)
                {
                    if (attrPtg.IsSum)
                    {
                        // Excel prefers To encode 'SUM()' as a tAttr Token, but this evaluator
                        // expects the equivalent function Token
                        //byte nArgs = 1;  // tAttrSum always Has 1 parameter
                        ptg = FuncVarPtg.SUM;//.Create("SUM", nArgs);
                    }
                    if (attrPtg.IsOptimizedChoose)
                    {
                        ValueEval arg0 = stack.Pop();
                        int[] jumpTable = attrPtg.JumpTable;
                        int dist;
                        int nChoices = jumpTable.Length;
                        try
                        {
                            int switchIndex = Choose.EvaluateFirstArg(arg0, ec.RowIndex, ec.ColumnIndex);
                            if (switchIndex < 1 || switchIndex > nChoices)
                            {
                                stack.Push(ErrorEval.VALUE_INVALID);
                                dist = attrPtg.ChooseFuncOffset + 4; // +4 for tFuncFar(CHOOSE)
                            }
                            else
                            {
                                dist = jumpTable[switchIndex - 1];
                            }
                        }
                        catch (EvaluationException e)
                        {
                            stack.Push(e.GetErrorEval());
                            dist = attrPtg.ChooseFuncOffset + 4; // +4 for tFuncFar(CHOOSE)
                        }
                        // Encoded dist for tAttrChoose includes size of jump table, but
                        // countTokensToBeSkipped() does not (it counts whole tokens).
                        dist -= nChoices * 2 + 2; // subtract jump table size
                        i += CountTokensToBeSkipped(ptgs, i, dist);
                        continue;
                    }
                    if (attrPtg.IsOptimizedIf)
                    {
                        if (!evaluationCell.IsPartOfArrayFormulaGroup)
                        {
                           ValueEval arg0 = stack.Pop();
                           bool evaluatedPredicate;
                           try
                           {
                               evaluatedPredicate = IfFunc.EvaluateFirstArg(arg0, ec.RowIndex, ec.ColumnIndex);
                           }
                           catch (EvaluationException e)
                           {
                               stack.Push(e.GetErrorEval());
                               int dist = attrPtg.Data;
                               i += CountTokensToBeSkipped(ptgs, i, dist);
                               attrPtg = (AttrPtg)ptgs[i];
                               dist = attrPtg.Data + 1;
                               i += CountTokensToBeSkipped(ptgs, i, dist);
                               continue;
                           }
                           if (evaluatedPredicate)
                           {
                               // nothing to skip - true param folows
                           }
                           else
                           {
                               int dist = attrPtg.Data;
                               i += CountTokensToBeSkipped(ptgs, i, dist);
                               Ptg nextPtg = ptgs[i + 1];
                               if (ptgs[i] is AttrPtg && nextPtg is FuncVarPtg varPtg &&
                                   // in order to verify that there is no third param, we need to check 
                                   // if we really have the IF next or some other FuncVarPtg as third param, e.g. ROW()/COLUMN()!
                                   varPtg.FunctionIndex == FunctionMetadataRegistry.FUNCTION_INDEX_IF)
                               {
                                   // this is an if statement without a false param (as opposed to MissingArgPtg as the false param)
                                   //i++;
                                   stack.Push(arg0);
                                   stack.Push(BoolEval.FALSE);
                               }
                           }
                        }
                        continue;
                    }
                    if (attrPtg.IsSkip && !evaluationCell.IsPartOfArrayFormulaGroup)
                    {
                        int dist = attrPtg.Data + 1;
                        i += CountTokensToBeSkipped(ptgs, i, dist);
                        if (stack.Peek() == MissingArgEval.instance)
                        {
                            stack.Pop();
                            stack.Push(BlankEval.instance);
                        }
                        continue;
                    }
                }
                if (ptg is ControlPtg)
                {
                    // skip Parentheses, Attr, etc
                    continue;
                }
                if (ptg is MemFuncPtg|| ptg is MemAreaPtg)
                {
                    // can ignore, rest of Tokens for this expression are in OK RPN order
                    continue;
                }
                if (ptg is MemErrPtg) 
                { 
                    continue; 
                }
                if (ptg is UnionPtg)
                {
                    ValueEval v2 = stack.Pop();
                    ValueEval v1 = stack.Pop();
                    stack.Push(new RefListEval(v1, v2));
                    continue;
                }
                ValueEval opResult;
                if (ptg is OperationPtg optg)
                {
                    int numops = optg.NumberOfOperands;
                    ValueEval[] ops = new ValueEval[numops];

                    // storing the ops in reverse order since they are popping
                    for (int j = numops - 1; j >= 0; j--)
                    {
                        ValueEval p = (ValueEval)stack.Pop();
                        ops[j] = p;
                    }
                    //				logDebug("Invoke " + operation + " (nAgs=" + numops + ")");
                    opResult = OperationEvaluatorFactory.Evaluate(optg, ops, ec);
                }
                else
                {
                    opResult = GetEvalForPtg(ptg, ec);
                }
                if (opResult == null)
                {
                    throw new Exception("Evaluation result must not be null");
                }
                //			logDebug("push " + opResult);
                stack.Push(opResult);
                if (dbgEvaluationOutputIndent > 0)
                {
                    EVAL_LOG.Log(POILogger.INFO, dbgIndentStr + "    = " + opResult.ToString());
                }
            }

            ValueEval value = ((ValueEval)stack.Pop());
            if (stack.Count != 0)
            {
                throw new InvalidOperationException("evaluation stack not empty");
            }

            ValueEval result;
            if (ec.IsSingleValue)
            {
                result = DereferenceResult(value, ec);
            }
            else
            {
                result = value;
            }
            if (dbgEvaluationOutputIndent > 0)
            {
                EVAL_LOG.Log(POILogger.INFO, dbgIndentStr + "finshed eval of "
                                + new CellReference(ec.RowIndex, ec.ColumnIndex).FormatAsString()
                                + ": " + result.ToString());
                dbgEvaluationOutputIndent--;
                if (dbgEvaluationOutputIndent == 1)
                {
                    // this evaluation is done, reset indent to stop logging
                    dbgEvaluationOutputIndent = -1;
                }
            } // if
            return result;
        }
        /**
         * Calculates the number of tokens that the evaluator should skip upon reaching a tAttrSkip.
         *
         * @return the number of tokens (starting from <c>startIndex+1</c>) that need to be skipped
         * to achieve the specified <c>distInBytes</c> skip distance.
         */
        private static int CountTokensToBeSkipped(Ptg[] ptgs, int startIndex, int distInBytes)
        {
            int remBytes = distInBytes;
            int index = startIndex;
            while (remBytes != 0)
            {
                index++;
                remBytes -= ptgs[index].Size;
                if (remBytes < 0)
                {
                    throw new Exception("Bad skip distance (wrong token size calculation).");
                }
                if (index >= ptgs.Length)
                {
                    throw new Exception("Skip distance too far (ran out of formula tokens).");
                }
            }
            return index - startIndex;
        }
        /**
         * Dereferences a single value from any AreaEval or RefEval evaluation result.
         * If the supplied evaluationResult is just a plain value, it is returned as-is.
         * @return a <c>NumberEval</c>, <c>StringEval</c>, <c>BoolEval</c>,
         *  <c>BlankEval</c> or <c>ErrorEval</c>. Never <c>null</c>.
         */
        public static ValueEval DereferenceResult(ValueEval evaluationResult, int srcRowNum, int srcColNum)
        {
            ValueEval value;
            try
            {
                value = OperandResolver.GetSingleValue(evaluationResult, srcRowNum, srcColNum);
            }
            catch (EvaluationException e)
            {
                return e.GetErrorEval();
            }
            if (value == BlankEval.instance)
            {
                // Note Excel behaviour here. A blank value is converted To zero.
                return NumberEval.ZERO;
                // Formulas _never_ evaluate To blank.  If a formula appears To have evaluated To
                // blank, the actual value is empty string. This can be verified with ISBLANK().
            }
            return value;
        }
        /**
         * Dereferences a single value from any AreaEval or RefEval evaluation
         * result. If the supplied evaluationResult is just a plain value, it is
         * returned as-is.
         *
         * @return a {@link NumberEval}, {@link StringEval}, {@link BoolEval}, or
         *         {@link ErrorEval}. Never <code>null</code>. {@link BlankEval} is
         *         converted to {@link NumberEval#ZERO}
         */
        public static ValueEval DereferenceResult(ValueEval evaluationResult, OperationEvaluationContext ec)
        {
            ValueEval value;

            if (ec == null)
            {
                throw new ArgumentNullException("OperationEvaluationContext ec is null");
            }
            if (ec.GetWorkbook() == null)
            {
                throw new ArgumentNullException("OperationEvaluationContext ec.getWorkbook() is null");
            }

            IEvaluationSheet evalSheet = ec.GetWorkbook().GetSheet(ec.SheetIndex);
            IEvaluationCell evalCell = evalSheet.GetCell(ec.RowIndex, ec.ColumnIndex);

            if (evalCell.IsPartOfArrayFormulaGroup && evaluationResult is AreaEval eval)
            {
                value = OperandResolver.GetElementFromArray(eval, evalCell);
            }
            else
            {
                value = DereferenceResult(evaluationResult, ec.RowIndex, ec.ColumnIndex);
            }

            return value;
        }
        /**
         * returns an appropriate Eval impl instance for the Ptg. The Ptg must be
         * one of: Area3DPtg, AreaPtg, ReferencePtg, Ref3DPtg, IntPtg, NumberPtg,
         * StringPtg, BoolPtg <br/>special Note: OperationPtg subtypes cannot be
         * passed here!
         */
        private ValueEval GetEvalForPtg(Ptg ptg, OperationEvaluationContext ec)
        {
            //  consider converting all these (ptg is XxxPtg) expressions To (ptg.GetType() == XxxPtg.class)

            if (ptg is NamePtg namePtg)
            {
                // Named ranges, macro functions
                IEvaluationName nameRecord = _workbook.GetName(namePtg);
                return GetEvalForNameRecord(nameRecord, ec);
            }
            if (ptg is NameXPtg xPtg) 
            {
                // Externally defined named ranges or macro functions
                return ProcessNameEval(ec.GetNameXEval(xPtg), ec);
            }
            if (ptg is NameXPxg pxg)
            {
                // Externally defined named ranges or macro functions
                return ProcessNameEval(ec.GetNameXEval(pxg), ec);
            }
            if (ptg is IntPtg intPtg)
            {
                return new NumberEval(intPtg.Value);
            }
            if (ptg is NumberPtg numberPtg)
            {
                return new NumberEval(numberPtg.Value);
            }
            if (ptg is StringPtg stringPtg)
            {
                return new StringEval(stringPtg.Value);
            }
            if (ptg is BoolPtg boolPtg)
            {
                return BoolEval.ValueOf(boolPtg.Value);
            }
            if (ptg is ErrPtg errPtg)
            {
                return ErrorEval.ValueOf(errPtg.ErrorCode);
            }
            if (ptg is MissingArgPtg)
            {
                return MissingArgEval.instance;
            }
            if (ptg is AreaErrPtg || ptg is RefErrorPtg
                    || ptg is DeletedArea3DPtg || ptg is DeletedRef3DPtg)
            {
                return ErrorEval.REF_INVALID;
            }
            if (ptg is Ref3DPtg ref3DPtg)
            {
                return ec.GetRef3DEval(ref3DPtg);
            }

            if (ptg is Ref3DPxg ref3DPxg)
            {
                return ec.GetRef3DEval(ref3DPxg);
            }
            if (ptg is Area3DPtg area3DPtg) {
               return ec.GetArea3DEval(area3DPtg);
           }
           if (ptg is Area3DPxg area3DPxg) {
               return ec.GetArea3DEval(area3DPxg);
           }

            if (ptg is RefPtg rptg)
            {
                return ec.GetRefEval(rptg.Row, rptg.Column);
            }
            if (ptg is AreaPtg areaPtg)
            {
                return ec.GetAreaEval(areaPtg.FirstRow, areaPtg.FirstColumn, areaPtg.LastRow, areaPtg.LastColumn);
            }
            if (ptg is ArrayPtg aptg) {
                return ec.GetAreaValueEval(0, 0, aptg.RowCount - 1, aptg.ColumnCount - 1, aptg.GetTokenArrayValues());
            }

            if (ptg is UnknownPtg)
            {
                // POI uses UnknownPtg when the encoded Ptg array seems To be corrupted.
                // This seems To occur in very rare cases (e.g. unused name formulas in bug 44774, attachment 21790)
                // In any case, formulas are re-parsed before execution, so UnknownPtg should not Get here
                throw new RuntimeException("UnknownPtg not allowed");
            }
            if (ptg is ExpPtg)
            {
                // ExpPtg is used for array formulas and shared formulas.
                // it is currently unsupported, and may not even get implemented here
                throw new RuntimeException("ExpPtg currently not supported");
            }
            throw new RuntimeException("Unexpected ptg class (" + ptg.GetType().Name + ")");
        }

        private ValueEval ProcessNameEval(ValueEval eval, OperationEvaluationContext ec)
        {
            if (eval is ExternalNameEval nameEval)
            {
                IEvaluationName name = nameEval.Name;
                return GetEvalForNameRecord(name, ec);
            }
            return eval;
        }
        private ValueEval GetEvalForNameRecord(IEvaluationName nameRecord, OperationEvaluationContext ec)
        {
            if (nameRecord.IsFunctionName)
            {
                return new FunctionNameEval(nameRecord.NameText);
            }
            if (nameRecord.HasFormula)
            {
                return EvaluateNameFormula(nameRecord.NameDefinition, ec);
            }

            throw new Exception("Don't now how to Evalate name '" + nameRecord.NameText + "'");
        }
        
        internal ValueEval EvaluateNameFormula(Ptg[] ptgs, OperationEvaluationContext ec)
        {
            if (ptgs.Length == 1)
            {
                return GetEvalForPtg(ptgs[0], ec);
            }
            return EvaluateFormula(ec, ptgs);
        }

        /**
         * Used by the lazy ref evals whenever they need To Get the value of a contained cell.
         */
        /* package */
        public ValueEval EvaluateReference(IEvaluationSheet sheet, int sheetIndex, int rowIndex,
            int columnIndex, EvaluationTracker tracker)
        {

            IEvaluationCell cell = sheet.GetCell(rowIndex, columnIndex);
            return EvaluateAny(cell, sheetIndex, rowIndex, columnIndex, tracker);
        }

        public FreeRefFunction FindUserDefinedFunction(String functionName)
        {
            return _udfFinder.FindFunction(functionName);
        }

        /**
         * Whether to ignore missing references to external workbooks and
         * use cached formula results in the main workbook instead.
         * <p>
         * In some cases exetrnal workbooks referenced by formulas in the main workbook are not avaiable.
         * With this method you can control how POI handles such missing references:
         * <ul>
         *     <li>by default ignoreMissingWorkbooks=false and POI throws {@link WorkbookNotFoundException}
         *     if an external reference cannot be resolved</li>
         *     <li>if ignoreMissingWorkbooks=true then POI uses cached formula result
         *     that already exists in the main workbook</li>
         * </ul>
         *</p>
         * @param ignore whether to ignore missing references to external workbooks
         * @see <a href="https://issues.apache.org/bugzilla/show_bug.cgi?id=52575">Bug 52575 for details</a>
         */
        public bool IgnoreMissingWorkbooks
        {
            get { return _ignoreMissingWorkbooks; }
            set { _ignoreMissingWorkbooks = value; }
        }

        /**
         * Return a collection of functions that POI can evaluate
         *
         * @return names of functions supported by POI
         */
        public static IList<String> GetSupportedFunctionNames()
        {
            List<String> lst = new List<String>();
            lst.AddRange(FunctionEval.GetSupportedFunctionNames());
            lst.AddRange(AnalysisToolPak.GetSupportedFunctionNames());
            return lst.AsReadOnly();
        }

        /**
         * Return a collection of functions that POI does not support
         *
         * @return names of functions NOT supported by POI
         */
        public static IList<String> GetNotSupportedFunctionNames()
        {
            List<String> lst = new List<String>();
            lst.AddRange(FunctionEval.GetNotSupportedFunctionNames());
            lst.AddRange(AnalysisToolPak.GetNotSupportedFunctionNames());
            return lst.AsReadOnly();
        }

        /**
         * Register a ATP function in runtime.
         *
         * @param name  the function name
         * @param func  the functoin to register
         * @throws IllegalArgumentException if the function is unknown or already  registered.
         * @since 3.8 beta6
         */
        public static void RegisterFunction(String name, FreeRefFunction func)
        {
            AnalysisToolPak.RegisterFunction(name, func);
        }

        /**
         * Register a function in runtime.
         *
         * @param name  the function name
         * @param func  the functoin to register
         * @throws IllegalArgumentException if the function is unknown or already  registered.
         * @since 3.8 beta6
         */
        public static void RegisterFunction(String name, Functions.Function func)
        {
            FunctionEval.RegisterFunction(name, func);
        }

        public bool DebugEvaluationOutputForNextEval
        {
            get { return dbgEvaluationOutputForNextEval; }
            set { dbgEvaluationOutputForNextEval = value; }
        }
    }
}
