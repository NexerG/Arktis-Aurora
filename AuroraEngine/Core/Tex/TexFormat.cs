namespace ArctisAurora.Core.Tex
{
    // The LaTeX commands that are plain macros over the typesetter's primitives, read before every source.
    public static class TexFormat
    {
        public const string Prelude = """
            \makeatletter
            \newcounter{part}\newcounter{chapter}\newcounter{section}[chapter]%
            \newcounter{subsection}[section]\newcounter{subsubsection}[subsection]%
            \newcounter{paragraph}[subsubsection]\newcounter{subparagraph}[paragraph]%
            \newcounter{footnote}\newcounter{figure}[chapter]\newcounter{table}[chapter]\newcounter{equation}[chapter]%
            \def\arabic#1{\expandafter\the\csname c@#1\endcsname}%
            \def\thesubsection{\thesection.\arabic{subsection}}\def\thesubsubsection{\thesubsection.\arabic{subsubsection}}%
            \def\theparagraph{\thesubsubsection.\arabic{paragraph}}\def\thesubparagraph{\theparagraph.\arabic{subparagraph}}%
            \dimendef\textwidth=250 \dimendef\linewidth=251 \dimendef\columnwidth=252 \dimendef\textheight=253 %
            \dimendef\paperwidth=254 \dimendef\paperheight=255 %
            \countdef\hyphenpenalty=250 \countdef\exhyphenpenalty=251 \countdef\tolerance=252 \countdef\pretolerance=253 %
            \hyphenpenalty=50 \exhyphenpenalty=50 \tolerance=200 \pretolerance=100 %
            \dimendef\parindent=249 \dimendef\emergencystretch=248 \skipdef\parskip=250 %
            \skipdef\topsep=246 \skipdef\partopsep=247 \skipdef\itemsep=248 \skipdef\parsep=249 %
            \parindent=15pt \parskip=0pt plus1pt\relax
            \countdef\hbadness=245 \countdef\widowpenalty=244 \dimendef\hfuzz=246 \dimendef\vfuzz=245 \countdef\c@page=242 %
            \hbadness=1000 \widowpenalty=150 \hfuzz=0.1pt \vfuzz=0.1pt \c@page=1 %
            \def\arraystretch{1}%
            \def\space{ }\newcommand\uline[1]{{\@underline#1}}\newcommand\hypersetup[1]{}%
            \def\raisebox#1{\@ifnextchar[\@rbopt\mbox}\def\@rbopt[#1]{\@ifnextchar[\@rbopt\mbox}%
            \def\captionsenglish{\def\figurename{Figure}\def\tablename{Table}\def\refname{References}\def\bibname{Bibliography}%
            \def\contentsname{Contents}\def\listfigurename{List of Figures}\def\listtablename{List of Tables}%
            \def\fnum@figure##1{\figurename\nobreakspace##1}\def\fnum@table##1{\tablename\nobreakspace##1}\def\proofname{Proof}}%
            \let\captionsamerican\captionsenglish\let\captionsusenglish\captionsenglish%
            \let\captionsbritish\captionsenglish\let\captionsUKenglish\captionsenglish%
            \def\captionslithuanian{\def\figurename{pav.}\def\tablename{lentelė}\def\refname{Literatūra}\def\bibname{Literatūra}%
            \def\contentsname{Turinys}\def\listfigurename{Paveikslų sąrašas}\def\listtablename{Lentelių sąrašas}%
            \def\fnum@figure##1{##1\nobreakspace\figurename}\def\fnum@table##1{##1\nobreakspace\tablename}\def\proofname{Įrodymas}}%
            \captionsenglish\def\caption@font{}\def\caption@lfont{}\def\caption@tfont{}\def\caption@sep{:\space}%
            \def\newblock{\hskip .11em plus .33em minus .07em\relax}%
            \def\descriptionlabel#1{\bfseries #1}\def\addto#1#2{\ifdefined#1\expandafter\def\expandafter#1\expandafter{#1#2}\else\def#1{#2}\fi}%
            \def\@Alph#1{\ifcase#1\or A\or B\or C\or D\or E\or F\or G\or H\or I\or J\or K\or L\or M\or N\or O\or P\or Q\or R\or S\or T\or U\or V\or W\or X\or Y\or Z\fi}%
            \def\Alph#1{\expandafter\@Alph\csname c@#1\endcsname}\def\appendix{\par\setcounter{section}{0}\gdef\thesection{\@Alph\c@section}}%
            \newlength\labelwidth\newlength\labelsep\newlength\leftmargin\def\@biblabel#1{[#1]}\def\@mkbiblab#1{\hfil#1}%
            \newlength\abovecaptionskip\setlength\abovecaptionskip{10pt}%
            \def\@biblabelsep{.5em}\def\@bibsetup#1{\labelsep\@biblabelsep\relax\settowidth\labelwidth{\@biblabel{#1}}\leftmargin\labelwidth
            \advance\leftmargin\labelsep\leftskip\leftmargin\parindent0pt\relax}%
            \def\@bibitem#1{\hskip-\leftskip\hb@xt@\labelwidth{\@mkbiblab{\@biblabel{#1}}}\hskip\labelsep\ignorespaces}%
            \newcommand\textbf[1]{{\bfseries#1}}\newcommand\textmd[1]{{\mdseries#1}}%
            \newcommand\textit[1]{{\itshape#1}}\newcommand\textsl[1]{{\slshape#1}}%
            \newcommand\textsc[1]{{\scshape#1}}\newcommand\textup[1]{{\upshape#1}}%
            \newcommand\textrm[1]{{\rmfamily#1}}\newcommand\textsf[1]{{\sffamily#1}}%
            \newcommand\texttt[1]{{\ttfamily#1}}\newcommand\textnormal[1]{{\normalfont#1}}%
            \newcommand\emph[1]{{\em#1}}\newcommand\underline[1]{{\@underline#1}}%
            \newcommand\textcolor[3][]{{\color[#1]{#2}#3}}%
            \def\bf{\normalfont\bfseries}\def\it{\normalfont\itshape}\def\sl{\normalfont\slshape}%
            \def\sc{\normalfont\scshape}\def\tt{\normalfont\ttfamily}\def\rm{\normalfont\rmfamily}%
            \def\sf{\normalfont\sffamily}%
            \def\title#1{\gdef\@title{#1}}\def\author#1{\gdef\@author{#1}}\def\date#1{\gdef\@date{#1}}%
            \def\@title{}\def\@author{}\def\@date{\today}\def\and{\quad}\def\thanks#1{}%
            \def\maketitle{\par\thispagestyle{plain}\vskip2em\begingroup\centering{\LARGE\@title\par}\vskip1.5em\large\@author\par\vskip1em\@date\par\endgroup\vskip1.5em}%
            \def\headrulewidth{0.4pt}\def\footrulewidth{0pt}%
            \def\DeclareMathOperator{\@ifstar{\@dmo*}{\@dmo{}}}\def\@dmo#1#2#3{\def#2{\operatorname#1{#3}}}%
            \newcommand\hspace{\@ifstar\@hspace\@hspace}\def\@hspace#1{\hskip#1\relax}%
            \newcommand\vspace{\@ifstar\@vspace\@vspace}\def\@vspace#1{\vskip#1\relax}%
            \def\hfill{\hskip0pt plus1fill\relax}\def\vfill{\vskip0pt plus1fill\relax}%
            \def\smallskip{\vskip3pt plus1pt minus1pt\relax}\def\medskip{\vskip6pt plus2pt minus2pt\relax}%
            \def\bigskip{\vskip12pt plus4pt minus4pt\relax}%
            \def\newline{\\}\newcommand\linebreak[1][]{\\}\newcommand\pagebreak[1][]{\newpage}%
            \def\cleardoublepage{\clearpage}%
            \def\enspace{\kern.5em}\def\thinspace{\kern.16667em}\def\negthinspace{\kern-.16667em}%
            \def~{\nobreakspace}\def\/{}%
            \skipdef\leftskip=243 \skipdef\rightskip=244 \skipdef\parfillskip=245 \dimendef\@tempdima=247 \countdef\interlinepenalty=246 %
            \let\protect\relax\def\leavevmode{\indent}\def\null{\hbox{}}\def\nobreak{\penalty10000 }\def\normalcolor{}\def\m@th{}%
            \def\hfil{\hskip0pt plus1fil\relax}\def\hss{\hskip0pt plus1fil minus1fil\relax}\def\hb@xt@{\hbox to}%
            \def\makebox{\@ifnextchar[\@makebox\mbox}\def\@makebox[#1]{\@ifnextchar[{\@imakebox[#1]}{\@imakebox[#1][c]}}%
            \def\@imakebox[#1][#2]#3{\hb@xt@#1{\csname @mb@#2\endcsname{#3}}}%
            \def\@mb@l#1{#1\hss}\def\@mb@r#1{\hss#1}\def\@mb@c#1{\hss#1\hss}\def\@mb@s#1{#1}%
            \newcounter{tocdepth}\setcounter{tocdepth}{3}\def\@mkboth#1#2{}\def\@toclevel{section}\def\@starttocsetup{}%
            \def\@pnumwidth{1.55em}\def\@tocrmarg{2.55em}\def\@dotsep{4.5}%
            \def\contentsline#1{\csname l@#1\endcsname}\def\numberline#1{\hb@xt@\@tempdima{#1\hfil}}%
            \def\@dottedtocline#1#2#3#4#5{\ifnum#1>\c@tocdepth\else\vskip0pt plus .2pt
            {\leftskip#2\relax\rightskip\@tocrmarg\parfillskip-\rightskip\parindent#2\relax\@afterindenttrue\interlinepenalty10000 \leavevmode
            \@tempdima#3\relax\advance\leftskip\@tempdima\null\nobreak\hskip-\leftskip{#4}\nobreak
            \leaders\hbox{$\m@th\mkern\@dotsep mu\hbox{.}\mkern\@dotsep mu$}\hfill\nobreak
            \hb@xt@\@pnumwidth{\hfil\normalfont\normalcolor#5}\par}\fi}%
            \def\l@section#1#2{\ifnum\c@tocdepth>0 \vskip1.0em plus1pt\@tempdima1.5em\begingroup
            \parindent0pt\rightskip\@pnumwidth\parfillskip-\@pnumwidth\leavevmode\bfseries\advance\leftskip\@tempdima
            \hskip-\leftskip#1\nobreak\hfil\nobreak\hb@xt@\@pnumwidth{\hss#2}\par\endgroup\fi}%
            \def\l@subsection{\@dottedtocline{2}{1.5em}{2.3em}}\def\l@subsubsection{\@dottedtocline{3}{3.8em}{3.2em}}%
            \def\l@paragraph{\@dottedtocline{4}{7.0em}{4.1em}}\def\l@subparagraph{\@dottedtocline{5}{10em}{5em}}%
            \def\l@figure{\@dottedtocline{1}{1.5em}{2.3em}}\let\l@table\l@figure
            \def\tableofcontents{\csname\@toclevel\endcsname*{\contentsname\@mkboth{}{}}\@starttoc{toc}}%
            \def\listoffigures{\csname\@toclevel\endcsname*{\listfigurename\@mkboth{}{}}\@starttoc{lof}}%
            \def\listoftables{\csname\@toclevel\endcsname*{\listtablename\@mkboth{}{}}\@starttoc{lot}}%
            \@pagedimens
            \makeatother
            """;

        // report and book: chapters head the contents, sections and below sit one level deeper
        public const string ChapterContents = """
            \gdef\@toclevel{chapter}%
            \gdef\l@chapter#1#2{\ifnum\c@tocdepth>-1 \vskip1.0em plus1pt\@tempdima1.5em\begingroup
            \parindent0pt\rightskip\@pnumwidth\parfillskip-\@pnumwidth\leavevmode\bfseries\advance\leftskip\@tempdima
            \hskip-\leftskip#1\nobreak\hfil\nobreak\hb@xt@\@pnumwidth{\hss#2}\par\endgroup\fi}%
            \gdef\l@section{\@dottedtocline{1}{1.5em}{2.3em}}\gdef\l@subsection{\@dottedtocline{2}{3.8em}{3.2em}}%
            \gdef\l@subsubsection{\@dottedtocline{3}{7.0em}{4.1em}}\gdef\l@paragraph{\@dottedtocline{4}{10em}{5em}}%
            \gdef\l@subparagraph{\@dottedtocline{5}{12em}{6em}}\gdef\l@figure{\@dottedtocline{1}{1.5em}{2.3em}}\global\let\l@table\l@figure
            \gdef\appendix{\par\setcounter{chapter}{0}\gdef\thechapter{\@Alph\c@chapter}}%
            """;

        // amsthm: theorem styles by body font, \newtheorem with a shared or a parent counter, proof
        public const string Amsthm = """
            \def\th@plain{\itshape}\def\th@definition{\normalfont}\def\th@remark{\normalfont}\let\@thmbody\th@plain
            \def\theoremstyle#1{\expandafter\let\expandafter\@thmbody\csname th@#1\endcsname}%
            \def\newtheoremstyle#1#2#3#4#5#6#7#8#9{\expandafter\def\csname th@#1\endcsname{#4}}\def\swapnumbers{}%
            \def\@thmhead#1#2{\par\vskip\topsep\noindent{\bfseries#1\ifx\relax#2\relax\else\ {\normalfont(#2)}\fi.}\ \ignorespaces}%
            \def\newtheorem{\@ifstar\@sthm\@nthm}%
            \def\@sthm#1#2{\expandafter\let\csname @thms@#1\endcsname\@thmbody
            \newenvironment{#1}[1][]{\@thmhead{#2}{##1}\csname @thms@#1\endcsname}{\par\vskip\topsep}}%
            \def\@nthm#1{\@ifnextchar[{\@othm{#1}}{\@ithm{#1}}}%
            \def\@othm#1[#2]#3{\@xthm{#1}{#2}{#3}}%
            \def\@ithm#1#2{\@ifnextchar[{\@wthm{#1}{#2}}{\newcounter{#1}\@xthm{#1}{#1}{#2}}}%
            \def\@wthm#1#2[#3]{\newcounter{#1}[#3]\expandafter\edef\csname the#1\endcsname{\expandafter\noexpand\csname the#3\endcsname.\noexpand\arabic{#1}}\@xthm{#1}{#1}{#2}}%
            \def\@xthm#1#2#3{\expandafter\let\csname @thms@#1\endcsname\@thmbody
            \newenvironment{#1}[1][]{\stepcounter{#2}\@thmhead{#3 \csname the#2\endcsname}{##1}\csname @thms@#1\endcsname}{\par\vskip\topsep}}%
            \def\qedsymbol{$\square$}\def\qed{\nobreak\hfill\qedsymbol}%
            \newenvironment{proof}[1][\proofname]{\par\vskip\topsep\noindent{\itshape#1.}\ \ignorespaces}{\qed\par\vskip\topsep}%
            """;

        // algorithm: its float's caption name, label and list; the counter and the environment come from the typesetter
        public const string Algorithm = """
            \def\ALG@name{Algorithm}\def\fname@algorithm{\ALG@name}\def\fnum@algorithm{\fname@algorithm\nobreakspace\thealgorithm}%
            \def\floatname#1#2{\expandafter\def\csname fname@#1\endcsname{#2}}%
            \def\listalgorithmname{List of \ALG@name s}\def\l@algorithm{\@dottedtocline{1}{1.5em}{2.3em}}%
            \def\listofalgorithms{\csname\@toclevel\endcsname*{\listalgorithmname\@mkboth{}{}}\@starttoc{loa}}%
            """;

        // biblatex: the punctuation commands a preamble's \DeclareDelimFormat code uses, and \labelalphaothers
        public const string Biblatex = """
            \def\addcomma{,}\def\addsemicolon{;}\def\addcolon{:}\def\addperiod{.}\def\adddot{.}\def\addslash{/}%
            \def\addspace{\space}\def\addabbrvspace{\space}\def\adddotspace{.\space}\def\addnbspace{\nobreakspace}%
            \def\isdot{}\def\newunitpunct{.\space}\def\labelalphaothers{+}\def\mkbibemph#1{\emph{#1}}%
            """;

        // tocloft: each level's indent, number width, fonts and leader as lengths and macros, set by one entry macro
        public const string Tocloft = """
            \def\cftdot{.}\def\cftdotsep{4.5}\def\cftnodots{5000}\def\cftpnumalign{r}%
            \newlength\cftparskip\setlength\cftparskip{0pt}\def\@starttocsetup{\parskip\cftparskip\relax}%
            \def\cftdotfill#1{\def\@tempa{#1}\def\@tempb{\cftnodots}\ifx\@tempa\@tempb\hfill
            \else\leaders\hbox{$\m@th\mkern#1 mu\hbox{\cftdot}\mkern#1 mu$}\hfill\fi}%
            \def\@cftlevel#1#2#3#4#5#6#7#8#9{\newlength#1\setlength#1{#5}\newlength#2\setlength#2{#6}\newlength#3\setlength#3{#7}%
            \expandafter\def\csname cft#4font\endcsname{#8}\expandafter\def\csname cft#4pagefont\endcsname{#8}%
            \expandafter\def\csname cft#4presnum\endcsname{}\expandafter\def\csname cft#4aftersnum\endcsname{}%
            \expandafter\def\csname cft#4aftersnumb\endcsname{}\expandafter\def\csname cft#4afterpnum\endcsname{}%
            \expandafter\def\csname cft#4dotsep\endcsname{#9}%
            \expandafter\edef\csname cft#4leader\endcsname{#8\noexpand\cftdotfill{\expandafter\noexpand\csname cft#4dotsep\endcsname}}}%
            \@cftlevel\cftbeforesecskip\cftsecindent\cftsecnumwidth{sec}{1.0em plus1pt}{0em}{1.5em}{\bfseries}{\cftnodots}%
            \@cftlevel\cftbeforesubsecskip\cftsubsecindent\cftsubsecnumwidth{subsec}{0pt}{1.5em}{2.3em}{\normalfont}{\cftdotsep}%
            \@cftlevel\cftbeforesubsubsecskip\cftsubsubsecindent\cftsubsubsecnumwidth{subsubsec}{0pt}{3.8em}{3.2em}{\normalfont}{\cftdotsep}%
            \@cftlevel\cftbeforeparaskip\cftparaindent\cftparanumwidth{para}{0pt}{7.0em}{4.1em}{\normalfont}{\cftdotsep}%
            \@cftlevel\cftbeforesubparaskip\cftsubparaindent\cftsubparanumwidth{subpara}{0pt}{10em}{5em}{\normalfont}{\cftdotsep}%
            \@cftlevel\cftbeforefigskip\cftfigindent\cftfignumwidth{fig}{0pt}{1.5em}{2.3em}{\normalfont}{\cftdotsep}%
            \@cftlevel\cftbeforetabskip\cfttabindent\cfttabnumwidth{tab}{0pt}{1.5em}{2.3em}{\normalfont}{\cftdotsep}%
            \def\@cftentry#1#2#3#4{\ifnum\c@tocdepth<#1 \else\vskip\csname cftbefore#2skip\endcsname
            {\leftskip\csname cft#2indent\endcsname\relax\rightskip\@tocrmarg\parfillskip-\rightskip
            \parindent\csname cft#2indent\endcsname\relax\@afterindenttrue\interlinepenalty10000 \leavevmode
            \@tempdima\csname cft#2numwidth\endcsname\relax
            \expandafter\let\expandafter\@cftbsnum\csname cft#2presnum\endcsname
            \expandafter\let\expandafter\@cftasnum\csname cft#2aftersnum\endcsname
            \expandafter\let\expandafter\@cftasnumb\csname cft#2aftersnumb\endcsname
            \advance\leftskip\@tempdima\null\nobreak\hskip-\leftskip{\csname cft#2font\endcsname#3}\nobreak
            {\csname cft#2leader\endcsname}\nobreak\makebox[\@pnumwidth][\cftpnumalign]{\csname cft#2pagefont\endcsname#4}%
            \csname cft#2afterpnum\endcsname\par}\fi}%
            \def\numberline#1{\hb@xt@\@tempdima{\@cftbsnum#1\@cftasnum\hfil}\@cftasnumb}%
            \def\l@section{\@cftentry1{sec}}\def\l@subsection{\@cftentry2{subsec}}\def\l@subsubsection{\@cftentry3{subsubsec}}%
            \def\l@paragraph{\@cftentry4{para}}\def\l@subparagraph{\@cftentry5{subpara}}%
            \def\l@figure{\@cftentry1{fig}}\def\l@table{\@cftentry1{tab}}%
            """;
    }
}
