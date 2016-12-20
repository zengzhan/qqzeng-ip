// zhouziqing / 233355@gmail.com
// 2016/12/21

package imapmq

import (
	"io/ioutil"
	"os"
	"path/filepath"
	"time"

	"github.com/emersion/go-imap"
	imap_client "github.com/emersion/go-imap/client"
	"github.com/veqryn/go-email/email"

	"github.com/mholt/archiver"
)

type IpDatMailboxUpdater struct {
	client *imap_client.Client
	mbox   *imap.MailboxStatus
	quit   chan bool
}

func NewIpDatMailboxUpdater(server, username, password string) (*IpDatMailboxUpdater, error) {
	// Connect to server
	c, err := imap_client.DialTLS(server+":993", nil)
	if err != nil {
		return nil, err
	}

	// Login
	if err := c.Login(username, password); err != nil {
		return nil, err
	}

	// Select INBOX
	mbox, err := c.Select("INBOX", false)
	if err != nil {
		return nil, err
	}

	return &IpDatMailboxUpdater{client: c, mbox: mbox}, nil
}

const tmpFilePath = "./tmp/"
const ipDatFilePath = "./lib/zengip.dat"

func (u *IpDatMailboxUpdater) parse(bean imap.Literal) {
	msg, err := email.ParseMessage(bean)
	if err != nil {
		panic(err)
	}
	for _, part := range msg.PartsContentTypePrefix("application/octet-stream") {
		if _, err := os.Stat(tmpFilePath); os.IsNotExist(err) {
			os.Mkdir(tmpFilePath, 0711)
		}
		_, contentDisposition, _ := part.Header.ContentDisposition()
		attrFileName := contentDisposition["filename"]
		rarFilePath := tmpFilePath + attrFileName
		unrarpath := rarFilePath[0 : len(rarFilePath)-len(filepath.Ext(rarFilePath))]

		err := ioutil.WriteFile(rarFilePath, part.Body, 0711)
		if err != nil {
			panic(err)
		}

		err = archiver.Rar.Open(rarFilePath, unrarpath)
		if err != nil {
			panic(err)
		}

		b, err := ioutil.ReadFile(unrarpath + "/qqzeng-ip-utf8.dat")
		if err != nil {
			panic(err)
		}

		err = ioutil.WriteFile(ipDatFilePath, b, 0711)
		if err != nil {
			panic(err)
		}

		os.Remove(rarFilePath)
		os.RemoveAll(unrarpath)
	}
}

func (s *IpDatMailboxUpdater) Close() {
	s.quit <- true
	s.client.LoggedOut()
	s.client.Close()
}

func (s *IpDatMailboxUpdater) fetch() {
	search := &imap.SearchCriteria{
		Unanswered: true,
		From:       "qqzeng-ip",
	}
	seq, err := s.client.Search(search)
	if err != nil {
		panic(err)
	}

	// 无新版本
	if len(seq) == 0 {
		return
	}

	seqset := new(imap.SeqSet)
	seqset.AddNum(seq...)

	ch := make(chan *imap.Message, 10)
	go func() {
		if err := s.client.Fetch(seqset, []string{imap.EnvelopeMsgAttr}, ch); err != nil {
			panic(err)
		}
	}()

	// 添加回复标记
	err = s.client.Store(seqset, "+FLAGS", imap.AnsweredFlag, nil)
	if err != nil {
		panic(err)
	}

	// 选取最新版
	var latestMsgTime time.Time
	var latestMsg *imap.Message
	for msg := range ch {
		if msg.Envelope.Date.After(latestMsgTime) {
			latestMsgTime = msg.Envelope.Date
			latestMsg = msg
		}
	}

	// 拉取邮件 body
	seqset.Clear()
	seqset.AddNum(latestMsg.SeqNum)
	msg := make(chan *imap.Message)
	go func() {
		if err := s.client.Fetch(seqset, []string{"body[]"}, msg); err != nil {
			panic(err)
		}
	}()

	latestMsg = <-msg

	s.parse(latestMsg.GetBody("BODY[]"))
}

func (s *IpDatMailboxUpdater) Run() {
	// 恢复标记 for debug
	//search := &imap.SearchCriteria{
	//	From: "qqzeng-ip",
	//}
	//seq, err := s.client.Search(search)
	//if err != nil {
	//	panic(err)
	//}
	//seqset := new(imap.SeqSet)
	//seqset.AddNum(seq...)
	//
	//s.client.Store(seqset, "-FLAGS", imap.AnsweredFlag, nil)

	go func() {
		for {
			select {
			case <-s.quit:
				return
			default:
				s.fetch()
			}
			// 每5分钟拉取一次
			time.Sleep(time.Second * 300)
		}
	}()
}
